// MediaBridge client for Unity (works in BepInEx / MelonLoader mods too).
// Drop this file into your mod. Then:
//
//   var bridge = MediaBridgeClient.Create();          // connects (and starts MediaBridge if installed)
//   bridge.MediaChanged += () => Debug.Log(bridge.Title + " - " + bridge.Artist);
//   bridge.PlayPause();  bridge.Next();  bridge.SetVolume(0.5f);
//
//   // Play the music in the game world with 3D audio:
//   var speaker = someGameObject.AddComponent<MediaBridgeSpeaker>();
//   speaker.Bridge = bridge;
//
// Full protocol: docs/PROTOCOL.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class MediaBridgeClient : MonoBehaviour
{
    public int ControlPort = 48900;
    public int AudioPort = 48901;

    // ----- latest state (read these any time on the main thread) -----
    public bool Connected { get; private set; }
    public string Title = "", Artist = "", Album = "", App = "", Status = "none";
    public double Position, Duration;
    public float Volume;
    public bool Muted;
    public string DeviceName = "";
    public Texture2D Artwork;

    /// <summary>Live song position in seconds (keeps counting between updates while playing).</summary>
    public double LivePosition
    {
        get
        {
            if (Status != "playing") return Position;
            var now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            return Math.Min(Duration, Position + (now - _positionUpdatedAt) / 1000.0);
        }
    }

    // ----- events (raised on the main thread) -----
    public event Action MediaChanged;
    public event Action VolumeChanged;
    public event Action DeviceChanged;
    public event Action ArtworkChanged;

    // ----- commands -----
    public void Play() { Send("{\"cmd\":\"play\"}"); }
    public void Pause() { Send("{\"cmd\":\"pause\"}"); }
    public void PlayPause() { Send("{\"cmd\":\"play_pause\"}"); }
    public void Next() { Send("{\"cmd\":\"next\"}"); }
    public void Previous() { Send("{\"cmd\":\"previous\"}"); }
    public void VolumeUp() { Send("{\"cmd\":\"volume_up\"}"); }
    public void VolumeDown() { Send("{\"cmd\":\"volume_down\"}"); }
    public void ToggleMute() { Send("{\"cmd\":\"toggle_mute\"}"); }
    /// <summary>0..1 — sets the Windows volume of the device the user picked (e.g. VB-Cable). Use for sliders.</summary>
    public void SetVolume(float value)
    {
        Send("{\"cmd\":\"set_volume\",\"value\":" + Mathf.Clamp01(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
    }

    public static MediaBridgeClient Create()
    {
        var go = new GameObject("MediaBridgeClient");
        DontDestroyOnLoad(go);
        return go.AddComponent<MediaBridgeClient>();
    }

    // =====================================================================

    [Serializable]
    class Msg
    {
        public string type, @event, error, message;
        public string app, title, artist, album, status, artworkId;
        public double position, duration;
        public long positionUpdatedAt;
        public float volume;
        public bool muted;
        public string name, mime, data;
    }

    readonly Queue<string> _incoming = new Queue<string>();
    readonly Queue<string> _outgoing = new Queue<string>();
    Thread _controlThread;
    volatile bool _running = true;
    double _positionUpdatedAt;
    string _artworkId;
    bool _triedLaunch;

    void Start()
    {
        _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "MediaBridge control" };
        _controlThread.Start();
    }

    void OnDestroy() { _running = false; }

    NetworkStream _stream;

    void Send(string json)
    {
        lock (_outgoing)
        {
            _outgoing.Enqueue(json);
            Flush();
        }
    }

    // Call with _outgoing locked. Commands sent while disconnected wait until we reconnect.
    void Flush()
    {
        if (_stream == null) return;
        try
        {
            while (_outgoing.Count > 0)
            {
                var b = Encoding.UTF8.GetBytes(_outgoing.Peek() + "\n");
                _stream.Write(b, 0, b.Length);
                _outgoing.Dequeue();
            }
        }
        catch (IOException) { _stream = null; }
        catch (ObjectDisposedException) { _stream = null; }
    }

    void ControlLoop()
    {
        while (_running)
        {
            try
            {
                using (var tcp = new TcpClient())
                {
                    tcp.Connect("127.0.0.1", ControlPort);
                    Connected = true;
                    var stream = tcp.GetStream();
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    lock (_outgoing)
                    {
                        _stream = stream;
                        _outgoing.Enqueue("{\"cmd\":\"hello\",\"name\":\"" + Application.productName + " mod\"}");
                        Flush();
                    }

                    // Block here reading messages; Send() writes from the main thread.
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                        lock (_incoming) _incoming.Enqueue(line);

                    lock (_outgoing) _stream = null;
                }
            }
            catch (SocketException)
            {
                if (!_triedLaunch) { _triedLaunch = true; TryLaunchMediaBridge(); }
            }
            catch (IOException) { }
            Connected = false;
            if (_running) Thread.Sleep(2000); // retry
        }
    }

    /// <summary>Starts MediaBridge if it's installed but not running.</summary>
    static void TryLaunchMediaBridge()
    {
        try
        {
            var info = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaBridge/bridge.json");
            if (!File.Exists(info)) { Debug.Log("[MediaBridge] Not installed. Get it from <your download link>."); return; }
            var path = JsonUtility.FromJson<BridgeInfo>(File.ReadAllText(info)).installPath;
            if (File.Exists(path)) System.Diagnostics.Process.Start(path);
        }
        catch (Exception e) { Debug.LogWarning("[MediaBridge] Could not start it: " + e.Message); }
    }

    [Serializable] class BridgeInfo { public string installPath; }

    void Update()
    {
        while (true)
        {
            string line;
            lock (_incoming)
            {
                if (_incoming.Count == 0) break;
                line = _incoming.Dequeue();
            }
            Handle(JsonUtility.FromJson<Msg>(line));
        }
    }

    void Handle(Msg m)
    {
        if (m.type == "event" && m.@event == "media")
        {
            Title = m.title; Artist = m.artist; Album = m.album; App = m.app; Status = m.status;
            Position = m.position; Duration = m.duration; _positionUpdatedAt = m.positionUpdatedAt;
            if (m.artworkId != _artworkId)
            {
                _artworkId = m.artworkId;
                if (string.IsNullOrEmpty(m.artworkId)) { Artwork = null; if (ArtworkChanged != null) ArtworkChanged(); }
                else Send("{\"cmd\":\"get_artwork\"}");
            }
            if (MediaChanged != null) MediaChanged();
        }
        else if (m.type == "event" && m.@event == "volume")
        {
            Volume = m.volume; Muted = m.muted;
            if (VolumeChanged != null) VolumeChanged();
        }
        else if (m.type == "event" && m.@event == "device")
        {
            DeviceName = m.name;
            if (DeviceChanged != null) DeviceChanged();
        }
        else if (m.type == "reply" && !string.IsNullOrEmpty(m.data))
        {
            if (Artwork == null) Artwork = new Texture2D(2, 2);
            Artwork.LoadImage(Convert.FromBase64String(m.data));
            if (ArtworkChanged != null) ArtworkChanged();
        }
        else if (m.type == "reply" && m.error != null && m.error != "no_artwork")
        {
            Debug.LogWarning("[MediaBridge] " + m.error + ": " + m.message);
        }
    }
}

/// <summary>
/// Plays MediaBridge's audio from this GameObject using Unity's 3D audio.
/// Add it to any object (a radio, a speaker, the player's car...).
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class MediaBridgeSpeaker : MonoBehaviour
{
    public MediaBridgeClient Bridge;
    public float TargetLatency = 0.10f; // seconds of audio kept buffered
    public float MaxLatency = 0.30f;    // drop audio beyond this, so delay can't build up

    AudioSource _source;
    Thread _thread;
    volatile bool _running = true;
    int _sampleRate;

    // Mono ring buffer filled by the network thread, drained by Unity's audio thread.
    float[] _ring = new float[48000 * 2];
    int _read, _write, _count;
    readonly object _lock = new object();
    bool _primed;

    void Start()
    {
        _source = GetComponent<AudioSource>();
        _source.spatialBlend = 1f; // fully 3D
        _source.loop = true;
        _thread = new Thread(AudioLoop) { IsBackground = true, Name = "MediaBridge audio" };
        _thread.Start();
    }

    void OnDestroy() { _running = false; }

    void Update()
    {
        // Create the streaming clip once we know the sample rate (from the stream header).
        if (_sampleRate > 0 && (_source.clip == null || _source.clip.frequency != _sampleRate))
        {
            _source.clip = AudioClip.Create("MediaBridge", _sampleRate, 1, _sampleRate, true, OnPcmRead);
            _source.Play();
        }
    }

    void OnPcmRead(float[] data)
    {
        lock (_lock)
        {
            int target = (int)(TargetLatency * _sampleRate);
            if (!_primed && _count >= target) _primed = true;
            for (int i = 0; i < data.Length; i++)
            {
                if (_primed && _count > 0)
                {
                    data[i] = _ring[_read];
                    _read = (_read + 1) % _ring.Length;
                    _count--;
                }
                else
                {
                    data[i] = 0f; // nothing playing (or not enough buffered yet)
                    _primed = false;
                }
            }
        }
    }

    void AudioLoop()
    {
        var header = new byte[16];
        var buf = new byte[8192];
        while (_running)
        {
            try
            {
                using (var tcp = new TcpClient())
                {
                    tcp.Connect("127.0.0.1", Bridge != null ? Bridge.AudioPort : 48901);
                    var s = tcp.GetStream();
                    ReadExactly(s, header);
                    if (Encoding.ASCII.GetString(header, 0, 4) != "MBA1") return;
                    _sampleRate = BitConverter.ToInt32(header, 4);
                    int channels = BitConverter.ToInt16(header, 8);
                    int frameBytes = 4 * channels;
                    int leftover = 0;

                    while (_running)
                    {
                        int n = s.Read(buf, leftover, buf.Length - leftover);
                        if (n <= 0) break;
                        n += leftover;
                        int frames = n / frameBytes;
                        lock (_lock)
                        {
                            for (int f = 0; f < frames; f++)
                            {
                                float sum = 0;
                                for (int c = 0; c < channels; c++) sum += BitConverter.ToSingle(buf, f * frameBytes + c * 4);
                                _ring[_write] = sum / channels; // mono for 3D
                                _write = (_write + 1) % _ring.Length;
                                if (_count < _ring.Length) _count++; else _read = (_read + 1) % _ring.Length;
                            }
                            // Too much buffered? Skip ahead so latency stays low.
                            int max = (int)(MaxLatency * _sampleRate), target = (int)(TargetLatency * _sampleRate);
                            if (_count > max)
                            {
                                int drop = _count - target;
                                _read = (_read + drop) % _ring.Length;
                                _count -= drop;
                            }
                        }
                        leftover = n - frames * frameBytes;
                        Buffer.BlockCopy(buf, frames * frameBytes, buf, 0, leftover);
                    }
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
            if (_running) Thread.Sleep(1000); // reconnect (e.g. after the user switches device)
        }
    }

    static void ReadExactly(Stream s, byte[] buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = s.Read(buffer, got, buffer.Length - got);
            if (n <= 0) throw new IOException("closed");
            got += n;
        }
    }
}
