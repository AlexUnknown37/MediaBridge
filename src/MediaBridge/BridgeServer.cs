using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MediaBridge;

/// <summary>
/// The local connection mods talk to. Two TCP ports, both only reachable from this PC (127.0.0.1):
///
///  Control port (default 48900): one JSON message per line, both ways. See docs/PROTOCOL.md.
///  Audio port   (default 48901): a 16-byte header, then a never-ending stream of float32 samples.
/// </summary>
sealed class BridgeServer
{
    public const int ProtocolVersion = 1;
    public static readonly string Version = typeof(BridgeServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    readonly Settings _settings;
    readonly MediaController _media;
    readonly AudioHub _audio;
    readonly List<ControlClient> _controlClients = [];
    readonly List<AudioClient> _audioClients = [];
    readonly CancellationTokenSource _stop = new();

    public event Action? ClientsChanged;
    public event Action<Action>? UserDeviceChange; // lets the tray app run device picks on its thread

    public BridgeServer(Settings settings, MediaController media, AudioHub audio)
    {
        _settings = settings;
        _media = media;
        _audio = audio;

        _media.Changed += state => Broadcast(Event("media", state));
        _audio.VolumeChanged += v => Broadcast(Event("volume", v));
        _audio.DeviceChanged += d => Broadcast(Event("device", d));
        _audio.FormatChanged += CloseAudioClients;
        _audio.AudioAvailable += SendAudio;
    }

    public int ControlClientCount { get { lock (_controlClients) return _controlClients.Count; } }
    public int AudioClientCount { get { lock (_audioClients) return _audioClients.Count; } }

    public void Start()
    {
        var control = new TcpListener(IPAddress.Loopback, _settings.ControlPort);
        var audio = new TcpListener(IPAddress.Loopback, _settings.AudioPort);
        control.Start();
        audio.Start();
        _ = AcceptLoop(control, HandleControlClient);
        _ = AcceptLoop(audio, HandleAudioClient);
        Log.Write($"Listening: control 127.0.0.1:{_settings.ControlPort}, audio 127.0.0.1:{_settings.AudioPort}");
    }

    public void Stop() => _stop.Cancel();

    async Task AcceptLoop(TcpListener listener, Func<TcpClient, Task> handler)
    {
        using var reg = _stop.Token.Register(listener.Stop);
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }
            client.NoDelay = true;
            _ = Task.Run(() => handler(client));
        }
    }

    // ================= control channel =================

    sealed class ControlClient(TcpClient tcp)
    {
        public readonly TcpClient Tcp = tcp;
        public readonly Channel<string> Outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        public string Name = "unknown mod";

        public void Send(JsonObject msg) => Outbox.Writer.TryWrite(msg.ToJsonString());
    }

    async Task HandleControlClient(TcpClient tcp)
    {
        var client = new ControlClient(tcp);
        lock (_controlClients) _controlClients.Add(client);
        ClientsChanged?.Invoke();

        var stream = tcp.GetStream();
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in client.Outbox.Reader.ReadAllAsync(_stop.Token))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch { tcp.Close(); }
        });

        // Say hello, then send the current state so the mod doesn't have to ask.
        client.Send(new JsonObject
        {
            ["type"] = "hello",
            ["name"] = "MediaBridge",
            ["version"] = Version,
            ["protocol"] = ProtocolVersion,
            ["audioPort"] = _settings.AudioPort,
        });
        client.Send(Event("media", _media.Snapshot()));
        client.Send(Event("volume", _audio.VolumeInfo()));
        client.Send(Event("device", _audio.DeviceInfo()));

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!_stop.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line == null) break;
                if (line.Length == 0) continue;
                if (line.Length > 64 * 1024) break; // nobody legit sends this much
                var reply = await HandleCommand(client, line);
                if (reply != null) client.Send(reply);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        finally
        {
            client.Outbox.Writer.TryComplete();
            tcp.Close();
            lock (_controlClients) _controlClients.Remove(client);
            Log.Write($"Mod disconnected: {client.Name}");
            ClientsChanged?.Invoke();
        }
        await writer;
    }

    async Task<JsonObject?> HandleCommand(ControlClient client, string line)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(line)?.AsObject(); }
        catch { return Reply(null, false, "bad_json", "That line was not valid JSON."); }
        if (msg == null) return Reply(null, false, "bad_json", "Expected a JSON object.");

        var id = msg["id"]?.DeepClone();
        var cmd = (string?)msg["cmd"] ?? "";

        try
        {
            switch (cmd)
            {
                case "hello":
                    client.Name = (string?)msg["name"] ?? client.Name;
                    Log.Write($"Mod connected: {client.Name}");
                    ClientsChanged?.Invoke();
                    return Reply(id, true);

                case "play" or "pause" or "play_pause" or "stop" or "next" or "previous":
                    return await _media.SendAsync(cmd)
                        ? Reply(id, true)
                        : Reply(id, false, "media_unavailable", "No media app is open, or it refused the command.");

                case "volume_up" or "volume_down":
                    var step = (float?)msg["step"] ?? _settings.VolumeStep;
                    return _audio.StepVolume(cmd == "volume_up" ? step : -step) ? Reply(id, true) : NoDevice(id);

                case "set_volume":
                    var value = (float?)msg["value"];
                    if (value == null) return Reply(id, false, "bad_args", "set_volume needs \"value\" from 0.0 to 1.0.");
                    return _audio.SetVolume(value.Value) ? Reply(id, true) : NoDevice(id);

                case "mute":
                    return _audio.SetMute((bool?)msg["value"] ?? true) ? Reply(id, true) : NoDevice(id);
                case "unmute":
                    return _audio.SetMute(false) ? Reply(id, true) : NoDevice(id);
                case "toggle_mute":
                    return _audio.SetMute(null) ? Reply(id, true) : NoDevice(id);

                case "get_state":
                    var state = Reply(id, true);
                    state["media"] = _media.Snapshot();
                    state["volume"] = _audio.VolumeInfo();
                    state["device"] = _audio.DeviceInfo();
                    return state;

                case "get_artwork":
                    var (data, mime) = _media.Artwork();
                    if (data == null) return Reply(id, false, "no_artwork", "The current song has no cover art.");
                    var art = Reply(id, true);
                    art["artworkId"] = _media.Snapshot()["artworkId"]?.DeepClone();
                    art["mime"] = mime;
                    art["data"] = Convert.ToBase64String(data);
                    return art;

                case "list_devices":
                    var list = Reply(id, true);
                    list["devices"] = _audio.ListDevices();
                    return list;

                case "select_device":
                    var devId = (string?)msg["deviceId"];
                    if (devId == null) return Reply(id, false, "bad_args", "select_device needs \"deviceId\" (from list_devices).");
                    var ok = false;
                    var done = new TaskCompletionSource();
                    UserDeviceChange?.Invoke(() =>
                    {
                        ok = _audio.TrySelect(devId);
                        if (ok) { _settings.DeviceId = devId; _settings.Save(); }
                        done.TrySetResult();
                    });
                    await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return ok ? Reply(id, true) : Reply(id, false, "bad_device", "That device is not available.");

                case "ping":
                    return Reply(id, true);

                default:
                    return Reply(id, false, "unknown_cmd", $"Unknown command \"{cmd}\".");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Command {cmd} failed: {ex}");
            return Reply(id, false, "error", ex.Message);
        }
    }

    static JsonObject NoDevice(JsonNode? id) => Reply(id, false, "no_device", "No audio device is selected.");

    static JsonObject Reply(JsonNode? id, bool ok, string? error = null, string? message = null)
    {
        var r = new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = ok };
        if (error != null) { r["error"] = error; r["message"] = message; }
        return r;
    }

    static JsonObject Event(string name, JsonObject data)
    {
        var e = new JsonObject { ["type"] = "event", ["event"] = name };
        foreach (var (k, v) in data) e[k] = v?.DeepClone();
        return e;
    }

    void Broadcast(JsonObject msg)
    {
        lock (_controlClients)
            foreach (var c in _controlClients) c.Send(msg);
    }

    // ================= audio channel =================

    sealed class AudioClient(TcpClient tcp)
    {
        public readonly TcpClient Tcp = tcp;
        // ~1 second of buffered packets; if a mod stops reading we drop old audio instead of using memory.
        public readonly Channel<byte[]> Outbox = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    async Task HandleAudioClient(TcpClient tcp)
    {
        if (_audio.SampleRate == 0) { tcp.Close(); return; } // nothing is being captured yet

        var client = new AudioClient(tcp);
        var stream = tcp.GetStream();
        tcp.SendBufferSize = 64 * 1024;

        // Header: "MBA1", sample rate (int32), channels (int16), sample format (int16: 3 = float32). Little-endian.
        var header = new byte[16];
        "MBA1"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), _audio.SampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(8), (short)_audio.Channels);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), 3);

        lock (_audioClients) _audioClients.Add(client);
        ClientsChanged?.Invoke();
        try
        {
            await stream.WriteAsync(header, _stop.Token);
            // Watch for the mod hanging up (it never sends us anything).
            _ = Task.Run(async () =>
            {
                var buf = new byte[64];
                try { while (await stream.ReadAsync(buf, _stop.Token) > 0) { } } catch { }
                client.Outbox.Writer.TryComplete();
            });
            await foreach (var packet in client.Outbox.Reader.ReadAllAsync(_stop.Token))
                await stream.WriteAsync(packet, _stop.Token);
        }
        catch { }
        finally
        {
            tcp.Close();
            lock (_audioClients) _audioClients.Remove(client);
            ClientsChanged?.Invoke();
        }
    }

    void SendAudio(byte[] buffer, int count)
    {
        lock (_audioClients)
        {
            if (_audioClients.Count == 0) return;
            var packet = buffer.AsSpan(0, count).ToArray();
            foreach (var c in _audioClients) c.Outbox.Writer.TryWrite(packet);
        }
    }

    void CloseAudioClients()
    {
        // The format may have changed; mods reconnect and read the new header.
        lock (_audioClients)
            foreach (var c in _audioClients) { c.Outbox.Writer.TryComplete(); c.Tcp.Close(); }
    }
}
