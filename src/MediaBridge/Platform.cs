using System.Text.Json.Nodes;

namespace MediaBridge;

// The two jobs that work differently on each operating system. The rest of MediaBridge (the mod
// connection, the radio stream, the script bridge) only talks to these, so it is the same everywhere.
//
//   Windows: MediaController (Windows media controls)  + AudioHub (WASAPI)
//   Linux:   MprisMediaController (MPRIS over D-Bus)   + PulseAudioHub (PipeWire / PulseAudio)

/// <summary>Play/pause/skip and "what's playing" for whatever music app is active.</summary>
interface IMediaController
{
    /// <summary>Raised (on a background thread) whenever song info or play state changes.</summary>
    event Action<JsonObject>? Changed;
    Task StartAsync();
    /// <summary>A copy of the latest media info (fields documented in docs/PROTOCOL.md, "media" event).</summary>
    JsonObject Snapshot();
    (byte[]? Data, string? Mime) Artwork();
    /// <summary>play, pause, play_pause, stop, next, previous. Returns false if no app took it.</summary>
    Task<bool> SendAsync(string command);
}

/// <summary>The one audio device the user picked: its live sound and its volume.</summary>
interface IAudioHub : IDisposable
{
    string? DeviceId { get; }
    string DeviceName { get; }
    /// <summary>Always 2 (stereo) once capturing; 0 before.</summary>
    int Channels { get; }
    int SampleRate { get; }

    /// <summary>Float32 interleaved stereo PCM bytes, raised on the capture thread.</summary>
    event Action<byte[], int>? AudioAvailable;
    /// <summary>The device or the audio format changed; open audio streams must restart.</summary>
    event Action? FormatChanged;
    event Action<JsonObject>? VolumeChanged;
    event Action<JsonObject>? DeviceChanged;

    JsonArray ListDevices();
    void SelectInitial(string? savedId);
    bool TrySelect(string deviceId);
    JsonObject VolumeInfo();
    JsonObject DeviceInfo();
    bool SetVolume(float value);
    bool StepVolume(float delta);
    /// <summary>true = mute, false = unmute, null = toggle.</summary>
    bool SetMute(bool? muted);
}
