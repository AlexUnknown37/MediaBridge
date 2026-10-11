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
/// </summary>
sealed class ScriptBridge
{
    public const string PipeName = "MediaBridge";
    static readonly string NowPlayingPath = Path.Combine(Settings.DataDir, "nowplaying.txt");

    readonly MediaController _media;
    readonly AudioHub _audio;
    readonly CancellationTokenSource _stop = new();
    long _seq;
    readonly object _writeGate = new();
    string? _lastText;

    public ScriptBridge(MediaController media, AudioHub audio)
    {
        _media = media;
        _audio = audio;
        _media.Changed += WriteNowPlaying;
    }

    public void Start()
    {
        WriteNowPlaying(_media.Snapshot());
        _ = Task.Run(PipeLoop);
        Log.Write($@"Script bridge: \\.\pipe\{PipeName}, {NowPlayingPath}");
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
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            var tmp = NowPlayingPath + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, NowPlayingPath, true);
        }
        catch (Exception ex) { Log.Write($"Could not write nowplaying.txt: {ex.Message}"); }
    }
}
