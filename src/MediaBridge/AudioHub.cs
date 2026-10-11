using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MediaBridge;

/// <summary>
/// Owns the one audio device the user picked: records its sound and controls its Windows volume.
///
/// For an output device (e.g. "CABLE Input", where Spotify is sent) we record what is being played
/// into it ("loopback"). For an input device (e.g. "CABLE Output", or a mic) we record it directly.
/// Audio is always handed out as 32-bit float, interleaved, in the device's own sample rate.
/// </summary>
sealed class AudioHub : IAudioHub
{
    readonly MMDeviceEnumerator _enumerator = new();
    readonly object _gate = new();

    MMDevice? _device;
    AudioEndpointVolume? _volume; // kept as a field so Windows keeps sending us volume notifications
    WasapiCapture? _capture;
    WaveFormat? _sourceFormat;
    System.Threading.Timer? _retryTimer;
    bool _disposed;

    public string? DeviceId { get; private set; }
    public string DeviceName { get; private set; } = "(none)";
    public string DeviceKind { get; private set; } = "none";
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    /// <summary>Float32 interleaved PCM bytes, raised on the capture thread.</summary>
    public event Action<byte[], int>? AudioAvailable;
    /// <summary>The device or the audio format changed; open audio streams must restart.</summary>
    public event Action? FormatChanged;
    public event Action<JsonObject>? VolumeChanged;
    public event Action<JsonObject>? DeviceChanged;

    public JsonArray ListDevices()
    {
        var list = new JsonArray();
        foreach (var (flow, kind) in new[] { (DataFlow.Render, "output"), (DataFlow.Capture, "input") })
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d)
                {
                    list.Add((JsonNode)new JsonObject
                    {
                        ["id"] = d.ID,
                        ["name"] = d.FriendlyName,
                        ["kind"] = kind,
                        ["selected"] = d.ID == DeviceId,
                        ["isVirtualCable"] = IsVirtualCable(d.FriendlyName),
                    });
                }
            }
        }
        return list;
    }

    /// <summary>Picks the saved device, or VB-Cable's "CABLE Input" if found, or the default speakers.</summary>
    public void SelectInitial(string? savedId)
    {
        if (savedId != null && TrySelect(savedId)) return;

        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (d)
            {
                if (d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) && TrySelect(d.ID)) return;
            }
        }
        if (_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var def) && def != null)
        {
            var id = def.ID;
            def.Dispose();
            TrySelect(id);
        }
    }

    public bool TrySelect(string deviceId)
    {
        MMDevice device;
        try
        {
            device = _enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) { device.Dispose(); return false; }
        }
        catch (Exception ex)
        {
            Log.Write($"Device {deviceId} not available: {ex.Message}");
            return false;
        }

        lock (_gate)
        {
            StopCaptureLocked();
            if (_volume != null) _volume.OnVolumeNotification -= OnVolumeNotification;
            _device?.Dispose();

            _device = device;
            DeviceId = device.ID;
            DeviceName = device.FriendlyName;
            DeviceKind = device.DataFlow == DataFlow.Render ? "output" : "input";
            _volume = device.AudioEndpointVolume;
            _volume.OnVolumeNotification += OnVolumeNotification;
            Log.Write($"Selected {DeviceKind} device: {DeviceName}");
            StartCaptureLocked();
        }

        DeviceChanged?.Invoke(DeviceInfo());
        VolumeChanged?.Invoke(VolumeInfo());
        return true;
    }

    // ---------- volume ----------

    public JsonObject VolumeInfo()
    {
        lock (_gate)
        {
            if (_volume == null) return new JsonObject { ["volume"] = 0.0, ["muted"] = false, ["deviceId"] = null };
            return new JsonObject
            {
                ["volume"] = Math.Round(_volume.MasterVolumeLevelScalar, 3),
                ["muted"] = _volume.Mute,
                ["deviceId"] = DeviceId,
            };
        }
    }

    public bool SetVolume(float value)
    {
        lock (_gate)
        {
            if (_volume == null) return false;
            _volume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            return true;
        }
    }

    public bool StepVolume(float delta)
    {
        lock (_gate)
        {
            if (_volume == null) return false;
            _volume.MasterVolumeLevelScalar = Math.Clamp(_volume.MasterVolumeLevelScalar + delta, 0f, 1f);
            return true;
        }
    }

    public bool SetMute(bool? muted)
    {
        lock (_gate)
        {
            if (_volume == null) return false;
            _volume.Mute = muted ?? !_volume.Mute;
            return true;
        }
    }

    void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        VolumeChanged?.Invoke(new JsonObject
        {
            ["volume"] = Math.Round(data.MasterVolume, 3),
            ["muted"] = data.Muted,
            ["deviceId"] = DeviceId,
        });
    }

    // ---------- capture ----------

    public JsonObject DeviceInfo() => new()
    {
        ["id"] = DeviceId,
        ["name"] = DeviceName,
        ["kind"] = DeviceKind,
        ["isVirtualCable"] = IsVirtualCable(DeviceName),
        ["sampleRate"] = SampleRate,
        ["channels"] = Channels,
        ["capturing"] = _capture != null,
    };

    void StartCaptureLocked()
    {
        if (_device == null || _disposed) return;
        try
        {
            var capture = _device.DataFlow == DataFlow.Render
                ? new WasapiLoopbackCapture(_device)
                : new WasapiCapture(_device, true, 20);
            _sourceFormat = capture.WaveFormat;
            SampleRate = _sourceFormat.SampleRate;
            Channels = 2; // always sent as stereo, see OnData
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
            Log.Write($"Capturing {DeviceName}: {SampleRate} Hz, {_sourceFormat.Channels} ch (sent as stereo), {_sourceFormat.BitsPerSample}-bit {_sourceFormat.Encoding}");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not start capture on {DeviceName}: {ex.Message}");
            _capture = null;
            ScheduleRetry();
        }
        FormatChanged?.Invoke();
    }

    void StopCaptureLocked()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        c.DataAvailable -= OnData;
        c.RecordingStopped -= OnStopped;
        try { c.StopRecording(); } catch { }
        c.Dispose();
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        Log.Write($"Capture stopped unexpectedly: {e.Exception.Message}");
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _capture)) return;
            StopCaptureLocked();
            ScheduleRetry();
        }
    }

    void ScheduleRetry()
    {
        _retryTimer?.Dispose();
        _retryTimer = new System.Threading.Timer(_ =>
        {
            lock (_gate)
            {
                if (_capture == null && !_disposed) StartCaptureLocked();
            }
        }, null, 2000, Timeout.Infinite);
    }

    byte[] _convertBuffer = [];

    void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _sourceFormat;
        var handler = AudioAvailable;
        if (fmt == null || handler == null || e.BytesRecorded == 0) return;

        int bits = fmt.BitsPerSample;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (fmt.Encoding == WaveFormatEncoding.Extensible && bits == 32 && IsFloatExtensible(fmt));

        int bytesPer = bits / 8;
        int samples = e.BytesRecorded / bytesPer;
        int channels = fmt.Channels;

        if (isFloat && bits == 32 && channels == 2)
        {
            handler(e.Buffer, e.BytesRecorded); // already what mods want
            return;
        }

        // Step 1: to float.
        if (_floatBuffer.Length < samples) _floatBuffer = new float[samples];
        var src = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int i = 0; i < samples; i++)
        {
            var s = src.Slice(i * bytesPer, bytesPer);
            _floatBuffer[i] = (isFloat, bits) switch
            {
                (true, 32) => BitConverter.ToSingle(s),
                (_, 16) => BitConverter.ToInt16(s) / 32768f,
                (_, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                (false, 32) => BitConverter.ToInt32(s) / 2147483648f,
                _ => 0f,
            };
        }

        // Step 2: to stereo. Windows channel order is FL, FR, FC, LFE, BL, BR, SL, SR.
        int frames = samples / channels;
        if (_convertBuffer.Length < frames * 8) _convertBuffer = new byte[frames * 8];
        var dst = MemoryMarshal.Cast<byte, float>(_convertBuffer.AsSpan(0, frames * 8));
        const float side = 0.7071f;
        for (int f = 0; f < frames; f++)
        {
            var x = _floatBuffer.AsSpan(f * channels, channels);
            float l, r;
            if (channels == 1) { l = r = x[0]; }
            else
            {
                l = x[0]; r = x[1];
                if (channels >= 3) { l += x[2] * side; r += x[2] * side; }
                for (int c = 4; c + 1 < channels; c += 2) { l += x[c] * side; r += x[c + 1] * side; }
            }
            dst[f * 2] = Math.Clamp(l, -1f, 1f);
            dst[f * 2 + 1] = Math.Clamp(r, -1f, 1f);
        }
        handler(_convertBuffer, frames * 8);
    }

    float[] _floatBuffer = [];

    static bool IsFloatExtensible(WaveFormat fmt) =>
        fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");

    static bool IsVirtualCable(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _retryTimer?.Dispose();
            StopCaptureLocked();
            if (_volume != null) _volume.OnVolumeNotification -= OnVolumeNotification;
            _device?.Dispose();
        }
        _enumerator.Dispose();
    }
}
