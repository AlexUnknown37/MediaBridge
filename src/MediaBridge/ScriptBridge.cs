using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace MediaBridge;

/// <summary>
/// For script-only mods (e.g. UE4SS Lua) that can't open TCP sockets but can open files:
///
///  Commands: write one command per line to the named pipe \\.\pipe\MediaBridge
///            (in Lua: local f = io.open("\\\\.\\pipe\\MediaBridge", "w"); f:write("next\n"); f:close())
///            Commands: play pause play_pause stop next previous volume_up volume_down toggle_mute
///  Song info: %LOCALAPPDATA%\MediaBridge\nowplaying.txt, rewritten on every change, one key=value per line
///            (title, artist, album, status, app, seq). "seq" goes up by one on every rewrite.
///
/// Linux, including Windows games running under Proton/Wine (they can't reach a Linux pipe):
///  Folder:    /tmp/MediaBridge (inside Proton: Z:\tmp\MediaBridge), plus ~/.local/share/MediaBridge
///  Commands:  append lines to commands.txt in that folder; MediaBridge reads new lines every 100 ms
///  Song info: nowplaying.txt in that folder, same format as on Windows
/// </summary>
sealed class ScriptBridge
{
    public const string PipeName = "MediaBridge";
    /// <summary>Folder a Proton/Wine game can reach as Z:\tmp\MediaBridge.</summary>
    public const string LinuxSharedDir = "/tmp/MediaBridge";

    /// <summary>Where nowplaying.txt (and on Linux, commands.txt) live.</summary>
    static readonly string[] Dirs = OperatingSystem.IsWindows() ? [Settings.DataDir] : [Settings.DataDir, LinuxSharedDir];

    readonly IMediaController _media;
    readonly IAudioHub _audio;
    readonly CancellationTokenSource _stop = new();
    long _seq;
    readonly object _writeGate = new();
    string? _lastText;

    public ScriptBridge(IMediaController media, IAudioHub audio)
    {
        _media = media;
        _audio = audio;
        _media.Changed += WriteNowPlaying;
    }

    public void Start()
    {
        WriteNowPlaying(_media.Snapshot());
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(PipeLoop);
            Log.Write($@"Script bridge: \\.\pipe\{PipeName}, {Settings.DataDir}\nowplaying.txt");
        }
        else
        {
            _ = Task.Run(CommandFileLoop);
            Log.Write($"Script bridge: {string.Join(", ", Dirs)} (commands.txt, nowplaying.txt)");
        }
    }

    public void Stop() => _stop.Cancel();

    async Task PipeLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch { pipe.Dispose(); throw; }
                // Handle this client in the background so the next pipe instance is listening right away.
                _ = Task.Run(() => HandleClient(pipe));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Write($"Script pipe error: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }

    /// <summary>Linux: picks up lines appended to commands.txt.</summary>
    async Task CommandFileLoop()
    {
        var offsets = new Dictionary<string, long>();
        foreach (var dir in Dirs)
        {
            var file = Path.Combine(dir, "commands.txt");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, ""); // start empty so old commands don't replay
            }
            catch (Exception ex) { Log.Write($"Could not prepare {file}: {ex.Message}"); }
            offsets[file] = 0;
        }

        while (!_stop.IsCancellationRequested)
        {
            foreach (var file in offsets.Keys.ToList())
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) { offsets[file] = 0; continue; }
                    if (info.Length < offsets[file]) offsets[file] = 0; // someone emptied it
                    if (info.Length == offsets[file]) continue;

                    string text;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Seek(offsets[file], SeekOrigin.Begin);
                        text = new StreamReader(fs, Encoding.UTF8).ReadToEnd();
                    }
                    // Only act on complete lines; a half-written line waits for the next round.
                    var end = text.LastIndexOf('\n');
                    if (end < 0) continue;
                    offsets[file] += Encoding.UTF8.GetByteCount(text[..(end + 1)]);
                    foreach (var line in text[..end].Split('\n'))
                        await Run(line.Trim());

                    // Keep the file small once everything in it has been handled.
                    if (offsets[file] > 64 * 1024 && new FileInfo(file).Length == offsets[file])
                    {
                        File.WriteAllText(file, "");
                        offsets[file] = 0;
                    }
                }
                catch (Exception ex) { Log.Write($"Reading {file} failed: {ex.Message}"); }
            }
            try { await Task.Delay(100, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }

    async Task HandleClient(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(_stop.Token)) != null)
                    await Run(line.Trim());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write($"Script pipe client error: {ex.Message}"); }
        catch (OperationCanceledException) { }
    }

    async Task Run(string cmd)
    {
        if (cmd.Length == 0) return;
        Log.Write($"Script command: {cmd}");
        switch (cmd)
        {
            case "play" or "pause" or "play_pause" or "stop" or "next" or "previous":
                await _media.SendAsync(cmd); break;
            case "volume_up": _audio.StepVolume(0.05f); break;
            case "volume_down": _audio.StepVolume(-0.05f); break;
            case "toggle_mute": _audio.SetMute(null); break;
        }
    }

    void WriteNowPlaying(JsonObject state)
    {
        static string Clean(JsonNode? n) => ((string?)n ?? "").Replace('\r', ' ').Replace('\n', ' ');
        var text = new StringBuilder()
            .Append("title=").AppendLine(Clean(state["title"]))
            .Append("artist=").AppendLine(Clean(state["artist"]))
            .Append("album=").AppendLine(Clean(state["album"]))
            .Append("status=").AppendLine(Clean(state["status"]))
            .Append("app=").AppendLine(Clean(state["app"]))
            .ToString();
        // Position updates arrive every few seconds; only rewrite when the song info itself changed.
        lock (_writeGate)
        {
            if (text == _lastText) return;
            _lastText = text;
        }
        text += $"seq={Interlocked.Increment(ref _seq)}\n";
        foreach (var dir in Dirs)
        {
            var path = Path.Combine(dir, "nowplaying.txt");
            try
            {
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            catch (Exception ex) { Log.Write($"Could not write {path}: {ex.Message}"); }
        }
    }
}
