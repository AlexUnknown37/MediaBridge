using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MediaBridge.Linux;

/// <summary>
/// Linux audio through PipeWire / PulseAudio (SteamOS and most desktops), using the standard `pactl` and
/// `pacat` tools.
///
/// Linux doesn't need VB-Cable: MediaBridge creates its own virtual speaker called "MediaBridge". Apps sent
/// there (by hand in the volume mixer, or automatically with "route apps") are recorded from its monitor.
/// Device IDs look like "sink:NAME" (outputs) and "source:NAME" (inputs).
/// </summary>
sealed class PulseAudioHub : IAudioHub
{
    public const string VirtualSinkName = "MediaBridge";
    const int Rate = 48000, Ch = 2;

    readonly object _gate = new();
    Process? _capture, _events;
    Thread? _captureThread;
    System.Threading.Timer? _retryTimer, _volumeDebounce;
    bool _disposed;
    string _lastVolumeKey = "";
    int? _loopbackModule;
    readonly HashSet<string> _routeApps = new(StringComparer.OrdinalIgnoreCase);

    public string? DeviceId { get; private set; }
    public string DeviceName { get; private set; } = "(none)";
    public string DeviceKind { get; private set; } = "none";
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public event Action<byte[], int>? AudioAvailable;
    public event Action? FormatChanged;
    public event Action<JsonObject>? VolumeChanged;
    public event Action<JsonObject>? DeviceChanged;

    public PulseAudioHub()
    {
        if (Proc.Run("pactl", "info") == null)
            Log.Write("pactl not found or no audio server: audio features won't work. (SteamOS/most desktops have it.)");
        StartEventWatcher();
    }

    // ---------------- devices ----------------

    record Device(string Kind, string Name, string Description, double Volume, bool Muted);

    static List<Device> Query(string kind) // kind: "sink" or "source"
    {
        var list = new List<Device>();
        var json = Proc.Run("pactl", "-f", "json", "list", kind + "s");
        if (json != null)
        {
            try
            {
                foreach (var d in JsonNode.Parse(json)!.AsArray())
                {
                    var name = (string)d!["name"]!;
                    if (kind == "source" && IsMonitor(d)) continue; // monitors are how we record outputs
                    list.Add(new Device(kind, name, (string?)d["description"] ?? name, AverageVolume(d["volume"]), (bool?)d["mute"] ?? false));
                }
                return list;
            }
            catch (Exception ex) { Log.Write($"Could not read pactl device list: {ex.Message}"); }
        }

        // Older pactl without JSON: names only.
        foreach (var line in (Proc.Run("pactl", "list", "short", kind + "s") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[1].EndsWith(".monitor")) continue;
            list.Add(new Device(kind, parts[1], parts[1], 0, false));
        }
        return list;
    }

    static bool IsMonitor(JsonNode d)
    {
        var of = (string?)d["monitor_of_sink"];
        return (of != null && of != "n/a") || ((string?)d["name"] ?? "").EndsWith(".monitor");
    }

    static double AverageVolume(JsonNode? volume)
    {
        if (volume is not JsonObject channels || channels.Count == 0) return 0;
        // Each channel: {"value": 42000, "value_percent": "64%", "db": "..."}; 65536 = 100%.
        return channels.Select(c => (double?)c.Value?["value"] ?? 0).Average() / 65536.0;
    }

    Device? Current()
    {
        if (DeviceId == null) return null;
        var (kind, name) = Split(DeviceId);
        return Query(kind).FirstOrDefault(d => d.Name == name);
    }

    static (string Kind, string Name) Split(string id)
    {
        var i = id.IndexOf(':');
        return i < 0 ? ("sink", id) : (id[..i], id[(i + 1)..]);
    }

    public JsonArray ListDevices()
    {
        var list = new JsonArray();
        foreach (var (kind, label) in new[] { ("sink", "output"), ("source", "input") })
        {
            foreach (var d in Query(kind))
            {
                var id = $"{kind}:{d.Name}";
                list.Add((JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["name"] = d.Description,
                    ["kind"] = label,
                    ["selected"] = id == DeviceId,
                    ["isVirtualCable"] = d.Name == VirtualSinkName,
                });
            }
        }
        return list;
    }

    /// <summary>Makes sure the "MediaBridge" virtual speaker exists (it disappears when the audio server restarts).</summary>
    public static void EnsureVirtualSink()
    {
        if (Query("sink").Any(d => d.Name == VirtualSinkName)) return;
        var result = Proc.Run("pactl", "load-module", "module-null-sink",
            $"sink_name={VirtualSinkName}", $"sink_properties=device.description={VirtualSinkName}");
        Log.Write(result != null ? "Created the MediaBridge virtual speaker" : "Could not create the MediaBridge virtual speaker");
    }

    public void SelectInitial(string? savedId)
    {
        if (savedId != null && TrySelect(savedId)) return;
        if (TrySelect($"sink:{VirtualSinkName}")) return;
        var def = Proc.Run("pactl", "get-default-sink")?.Trim();
        if (!string.IsNullOrEmpty(def)) TrySelect($"sink:{def}");
    }

    public bool TrySelect(string deviceId)
    {
        var (kind, name) = Split(deviceId);
        var device = Query(kind).FirstOrDefault(d => d.Name == name);
        if (device == null) return false;

        lock (_gate)
        {
            StopCaptureLocked();
            DeviceId = $"{kind}:{name}";
            DeviceName = device.Description;
            DeviceKind = kind == "sink" ? "output" : "input";
            Log.Write($"Selected {DeviceKind} device: {DeviceName} ({name})");
            StartCaptureLocked();
        }
        if (_routeApps.Count > 0) RouteNow();
        _lastVolumeKey = "";
        DeviceChanged?.Invoke(DeviceInfo());
        VolumeChanged?.Invoke(VolumeInfo());
        return true;
    }

    public JsonObject DeviceInfo() => new()
    {
        ["id"] = DeviceId,
        ["name"] = DeviceName,
        ["kind"] = DeviceKind,
        ["isVirtualCable"] = DeviceId == $"sink:{VirtualSinkName}",
        ["sampleRate"] = SampleRate,
        ["channels"] = Channels,
        ["capturing"] = _capture is { HasExited: false },
    };

    // ---------------- volume ----------------

    public JsonObject VolumeInfo()
    {
        var d = Current();
        return new JsonObject
        {
            ["volume"] = Math.Round(d?.Volume ?? 0, 3),
            ["muted"] = d?.Muted ?? false,
            ["deviceId"] = DeviceId,
        };
    }

    public bool SetVolume(float value)
    {
        if (DeviceId == null) return false;
        var (kind, name) = Split(DeviceId);
        var pct = Math.Round(Math.Clamp(value, 0f, 1f) * 100, 1).ToString(CultureInfo.InvariantCulture) + "%";
        return Proc.Run("pactl", $"set-{kind}-volume", name, pct) != null;
    }

    public bool StepVolume(float delta)
    {
        var d = Current();
        return d != null && SetVolume((float)d.Volume + delta);
    }

    public bool SetMute(bool? muted)
    {
        if (DeviceId == null) return false;
        var (kind, name) = Split(DeviceId);
        return Proc.Run("pactl", $"set-{kind}-mute", name, muted switch { true => "1", false => "0", null => "toggle" }) != null;
    }

    /// <summary>Watches the audio server for changes (volume, devices, new app streams).</summary>
    void StartEventWatcher()
    {
        var t = new Thread(() =>
        {
            while (!_disposed)
            {
                _events = Proc.Start("pactl", "subscribe");
                if (_events == null) return; // no pactl
                string? line;
                while ((line = _events.StandardOutput.ReadLine()) != null)
                {
                    // e.g. "Event 'change' on sink #57"   "Event 'new' on sink-input #120"
                    if (line.Contains(" on sink-input ") && line.Contains("'new'") && _routeApps.Count > 0)
                        Task.Delay(300).ContinueWith(_ => RouteNow());
                    else if (line.Contains(" on sink ") || line.Contains(" on source ") || line.Contains(" on server"))
                        QueueVolumeCheck();
                }
                if (!_disposed) Thread.Sleep(2000); // audio server restarted; resubscribe
            }
        }) { IsBackground = true, Name = "pactl subscribe" };
        t.Start();
    }

    void QueueVolumeCheck()
    {
        _volumeDebounce?.Dispose();
        _volumeDebounce = new System.Threading.Timer(_ =>
        {
            var v = VolumeInfo();
            var key = v.ToJsonString();
            if (key == _lastVolumeKey) return;
            _lastVolumeKey = key;
            VolumeChanged?.Invoke(v);
        }, null, 100, Timeout.Infinite);
    }

    // ---------------- extras Linux makes easy ----------------

    /// <summary>
    /// Automatically sends these apps (e.g. "spotify") into the picked output, also ones that start later.
    /// Turning it off sends them back to the normal speakers.
    /// </summary>
    public void SetRouteApps(IEnumerable<string> apps)
    {
        var old = _routeApps.ToList();
        _routeApps.Clear();
        foreach (var a in apps) if (!string.IsNullOrWhiteSpace(a)) _routeApps.Add(a.Trim());
        RouteNow();
        // Apps no longer routed go back to the default speakers.
        foreach (var removed in old.Where(a => !_routeApps.Contains(a)))
            foreach (var input in SinkInputs().Where(i => Matches(i.App, removed)))
                Proc.Run("pactl", "move-sink-input", input.Index, "@DEFAULT_SINK@");
    }

    public IReadOnlyCollection<string> RouteApps => _routeApps;

    void RouteNow()
    {
        if (DeviceId == null || _routeApps.Count == 0) return;
        var (kind, name) = Split(DeviceId);
        if (kind != "sink") return;
        foreach (var input in SinkInputs())
        {
            if (!_routeApps.Any(a => Matches(input.App, a)) || input.SinkName == name) continue;
            Log.Write($"Routing {input.App} into {name}");
            Proc.Run("pactl", "move-sink-input", input.Index, name);
        }
    }

    static bool Matches(string app, string wanted) => app.Contains(wanted, StringComparison.OrdinalIgnoreCase);

    record SinkInput(string Index, string App, string SinkName);

    static List<SinkInput> SinkInputs()
    {
        var result = new List<SinkInput>();
        var sinkJson = Proc.Run("pactl", "-f", "json", "list", "sinks");
        var sinkNames = new Dictionary<long, string>();
        try
        {
            if (sinkJson != null)
                foreach (var s in JsonNode.Parse(sinkJson)!.AsArray())
                    sinkNames[(long)s!["index"]!] = (string)s["name"]!;
        }
        catch { }

        var json = Proc.Run("pactl", "-f", "json", "list", "sink-inputs");
        if (json == null) return result;
        try
        {
            foreach (var i in JsonNode.Parse(json)!.AsArray())
            {
                var props = i!["properties"];
                var app = string.Join(' ', (string?)props?["application.name"], (string?)props?["application.process.binary"],
                    (string?)props?["application.id"], (string?)props?["pipewire.access.portal.app_id"]);
                var sinkIndex = (long?)i["sink"] ?? -1;
                result.Add(new SinkInput(((long)i["index"]!).ToString(CultureInfo.InvariantCulture), app, sinkNames.GetValueOrDefault(sinkIndex, "")));
            }
        }
        catch (Exception ex) { Log.Write($"Could not read app streams: {ex.Message}"); }
        return result;
    }

    /// <summary>Also play the picked output on the normal speakers, so you can hear it outside games.</summary>
    public bool SetListen(bool on)
    {
        if (!on)
        {
            if (_loopbackModule is int m) Proc.Run("pactl", "unload-module", m.ToString(CultureInfo.InvariantCulture));
            _loopbackModule = null;
            return true;
        }
        if (_loopbackModule != null) return true;
        if (DeviceId == null) return false;
        var (kind, name) = Split(DeviceId);
        if (kind != "sink" || name == Proc.Run("pactl", "get-default-sink")?.Trim()) return false; // would echo itself
        var id = Proc.Run("pactl", "load-module", "module-loopback", $"source={name}.monitor", "sink=@DEFAULT_SINK@", "latency_msec=60");
        if (int.TryParse(id?.Trim(), out var module)) _loopbackModule = module;
        return _loopbackModule != null;
    }

    public bool Listening => _loopbackModule != null;

    // ---------------- capture ----------------

    void StartCaptureLocked()
    {
        if (DeviceId == null || _disposed) return;
        var (kind, name) = Split(DeviceId);
        var device = kind == "sink" ? name + ".monitor" : name;

        _capture = Proc.Start("pacat", "--record", $"--device={device}", "--format=float32le", $"--rate={Rate}",
            $"--channels={Ch}", "--raw", "--latency-msec=20", "--client-name=MediaBridge", "--stream-name=capture");
        if (_capture == null)
        {
            Log.Write("pacat not found: can't record audio. (It comes with PulseAudio/PipeWire's libpulse.)");
            return;
        }
        SampleRate = Rate;
        Channels = Ch;
        var proc = _capture;
        _captureThread = new Thread(() => CaptureLoop(proc)) { IsBackground = true, Name = "pacat capture" };
        _captureThread.Start();
        Log.Write($"Capturing {device}: {Rate} Hz, {Ch} ch, float32");
        FormatChanged?.Invoke();
    }

    void CaptureLoop(Process proc)
    {
        var stream = proc.StandardOutput.BaseStream;
        var buffer = new byte[Rate * Ch * 4 / 50]; // 20 ms
        int filled = 0;
        try
        {
            int n;
            while ((n = stream.Read(buffer, filled, buffer.Length - filled)) > 0)
            {
                filled += n;
                int whole = filled - filled % (Ch * 4); // hand out whole frames only
                if (whole > 0)
                {
                    AudioAvailable?.Invoke(buffer, whole);
                    Buffer.BlockCopy(buffer, whole, buffer, 0, filled - whole);
                    filled -= whole;
                }
            }
        }
        catch (Exception ex) { Log.Write($"Capture read failed: {ex.Message}"); }

        lock (_gate)
        {
            if (!ReferenceEquals(proc, _capture) || _disposed) return; // stopped on purpose
            Log.Write("Capture stopped unexpectedly; retrying in 2 s");
            _capture = null;
            _retryTimer?.Dispose();
            _retryTimer = new System.Threading.Timer(_ => { lock (_gate) if (_capture == null && !_disposed) StartCaptureLocked(); }, null, 2000, Timeout.Infinite);
        }
    }

    void StopCaptureLocked()
    {
        var p = _capture;
        _capture = null;
        if (p == null) return;
        try { p.Kill(); } catch { }
        p.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _retryTimer?.Dispose();
            StopCaptureLocked();
        }
        SetListen(false);
        try { _events?.Kill(); } catch { }
    }
}
