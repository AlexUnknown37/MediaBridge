using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;

namespace MediaBridge;

/// <summary>
/// Serves the picked device's audio as an MP3 "internet radio station" at http://127.0.0.1:48902/MediaBridge.mp3
///
/// Games with an internet-radio feature (e.g. Pacific Drive's custom radio) can tune in to it and play the
/// music through their own 3D audio, without any audio modding. Encoding uses Windows' built-in MP3 encoder
/// and only runs while someone is listening.
/// </summary>
sealed class RadioServer
{
    const int SampleRate = 48000, Channels = 2, Bitrate = 192_000;

    readonly Settings _settings;
    readonly AudioHub _audio;
    readonly List<Listener> _listeners = [];
    readonly CancellationTokenSource _stop = new();
    readonly LivePcmProvider _pcm = new(SampleRate, Channels);
    readonly List<LivePcmProvider> _wavFeeds = [];
    Thread? _encoderThread;

    public RadioServer(Settings settings, AudioHub audio)
    {
        _settings = settings;
        _audio = audio;
        _audio.AudioAvailable += (buf, count) =>
        {
            _pcm.AddFloatStereo(buf.AsSpan(0, count), _audio.SampleRate);
            lock (_wavFeeds)
                foreach (var feed in _wavFeeds) feed.AddFloatStereo(buf.AsSpan(0, count), _audio.SampleRate);
        };
    }

    public string Url => $"http://127.0.0.1:{_settings.RadioPort}/MediaBridge.mp3";
    /// <summary>
    /// The same audio as uncompressed WAV. Windows Media Foundation players start it after ~3 s instead of ~6 s for MP3
    /// (they buffer longer for MP3), so it's the better choice for games that play streams through Media Foundation.
    /// </summary>
    public string WavUrl => $"http://127.0.0.1:{_settings.RadioPort}/MediaBridge.wav";
    public int ListenerCount { get { lock (_listeners) lock (_wavFeeds) return _listeners.Count + _wavFeeds.Count; } }
    public event Action? ListenersChanged;

    public void Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, _settings.RadioPort);
        listener.Start();
        _ = AcceptLoop(listener);
        Log.Write($"Radio stream: {Url}");
    }

    public void Stop() => _stop.Cancel();

    sealed class Listener(TcpClient tcp)
    {
        public readonly TcpClient Tcp = tcp;
        public readonly System.Threading.Channels.Channel<byte[]> Outbox =
            System.Threading.Channels.Channel.CreateBounded<byte[]>(
                new System.Threading.Channels.BoundedChannelOptions(200)
                { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
    }

    async Task AcceptLoop(TcpListener server)
    {
        using var reg = _stop.Token.Register(server.Stop);
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await server.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }
            _ = Task.Run(() => Serve(tcp));
        }
    }

    async Task Serve(TcpClient tcp)
    {
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        bool wav = false;
        try
        {
            // Read the request headers (we don't care what path was asked for).
            var request = await ReadRequestHead(stream);
            if (request == null) { tcp.Close(); return; }
            var firstLine = request.Split("\r\n", 2)[0];
            Log.Write($"Radio request: {firstLine}");
            wav = firstLine.Contains(".wav", StringComparison.OrdinalIgnoreCase);

            var headers =
                "HTTP/1.1 200 OK\r\n" +
                (wav ? "Content-Type: audio/wav\r\n" : "Content-Type: audio/mpeg\r\n") +
                "Cache-Control: no-cache, no-store\r\n" +
                "Pragma: no-cache\r\n" +
                "Connection: close\r\n" +
                "icy-name: MediaBridge\r\n" +
                "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
            if (firstLine.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase)) { tcp.Close(); return; }
        }
        catch { tcp.Close(); return; }

        if (wav) { await Task.Factory.StartNew(() => ServeWav(tcp, stream), TaskCreationOptions.LongRunning); return; }

        var listener = new Listener(tcp);
        lock (_listeners)
        {
            _listeners.Add(listener);
            EnsureEncoderRunning();
        }
        ListenersChanged?.Invoke();
        try
        {
            await foreach (var chunk in listener.Outbox.Reader.ReadAllAsync(_stop.Token))
                await stream.WriteAsync(chunk, _stop.Token);
        }
        catch { }
        finally
        {
            tcp.Close();
            lock (_listeners) _listeners.Remove(listener);
            Log.Write("Radio listener left");
            ListenersChanged?.Invoke();
        }
    }

    /// <summary>Streams raw 16-bit PCM in a WAV container with "endless" sizes. Runs on its own thread (the feed blocks).</summary>
    void ServeWav(TcpClient tcp, NetworkStream stream)
    {
        // Small lead: capture arrives in ~10 ms packets, so 60 ms is plenty and keeps the delay low.
        var feed = new LivePcmProvider(SampleRate, Channels, leadSeconds: 0.06);
        feed.Restart(stopWhen: () => _stop.IsCancellationRequested);
        lock (_wavFeeds) _wavFeeds.Add(feed);
        ListenersChanged?.Invoke();
        try
        {
            var fmt = feed.WaveFormat;
            using (var header = new MemoryStream())
            using (var w = new BinaryWriter(header))
            {
                w.Write("RIFF"u8); w.Write(-1); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)fmt.Channels);
                w.Write(fmt.SampleRate); w.Write(fmt.AverageBytesPerSecond); w.Write((short)fmt.BlockAlign); w.Write((short)16);
                w.Write("data"u8); w.Write(-1);
                w.Flush();
                stream.Write(header.ToArray());
            }
            var buffer = new byte[fmt.AverageBytesPerSecond / 50];
            int n;
            while ((n = feed.Read(buffer)) > 0) stream.Write(buffer, 0, n); // throws when the game hangs up
        }
        catch { }
        finally
        {
            tcp.Close();
            lock (_wavFeeds) _wavFeeds.Remove(feed);
            Log.Write("Radio listener left (wav)");
            ListenersChanged?.Invoke();
        }
    }

    static async Task<string?> ReadRequestHead(NetworkStream stream)
    {
        var buf = new byte[8192];
        int len = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (len < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(len), timeout.Token);
            if (n == 0) return null;
            len += n;
            var text = Encoding.ASCII.GetString(buf, 0, len);
            if (text.Contains("\r\n\r\n")) return text;
        }
        return null;
    }

    // ---------------- encoder ----------------

    void EnsureEncoderRunning()
    {
        if (_encoderThread is { IsAlive: true }) return;
        _encoderThread = new Thread(EncoderLoop) { IsBackground = true, Name = "MP3 encoder" };
        _encoderThread.Start();
    }

    bool HasListeners() { lock (_listeners) return _listeners.Count > 0; }

    void EncoderLoop()
    {
        Log.Write("MP3 encoder started");
        try
        {
            _pcm.Restart(stopWhen: () => !HasListeners() || _stop.IsCancellationRequested);
            var output = new BroadcastStream(Broadcast);
            MediaFoundationEncoder.EncodeToMp3(_pcm, output, Bitrate);
        }
        catch (Exception ex)
        {
            Log.Write($"MP3 encoder failed: {ex}");
        }
        Log.Write("MP3 encoder stopped");
        // A listener may have joined just as we were stopping.
        lock (_listeners)
            if (_listeners.Count > 0 && !_stop.IsCancellationRequested) EnsureEncoderRunningLater();
    }

    void EnsureEncoderRunningLater() => Task.Delay(200).ContinueWith(_ => { lock (_listeners) if (_listeners.Count > 0) EnsureEncoderRunning(); });

    void Broadcast(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var copy = data.ToArray();
        lock (_listeners)
            foreach (var l in _listeners) l.Outbox.Writer.TryWrite(copy);
    }

    /// <summary>
    /// A write-only stream that forwards whatever the encoder writes to all listeners. It pretends to be
    /// seekable because the encoder expects that; it only seeks back when finishing, which we never do.
    /// </summary>
    sealed class BroadcastStream(BroadcastStream.Sink sink) : Stream
    {
        public delegate void Sink(ReadOnlySpan<byte> data);
        long _position, _length;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        public override void SetLength(long value) => _length = value;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            // Only forward data appended at the end (ignore header rewrites at an earlier position).
            if (_position >= _length) sink(buffer);
            _position += buffer.Length;
            _length = Math.Max(_length, _position);
        }
    }
}

/// <summary>
/// Endless 16-bit PCM source for the encoder, paced to real time. Fills gaps with silence, so the
/// radio stream never stalls even when nothing is playing (Windows sends no audio during silence).
/// </summary>
sealed class LivePcmProvider : IWaveProvider
{
    readonly object _gate = new();
    readonly byte[] _ring;
    int _read, _count;
    readonly Stopwatch _clock = new();
    long _bytesDelivered;
    Func<bool> _stopWhen = () => false;
    readonly double _lead;

    public LivePcmProvider(int sampleRate, int channels, double leadSeconds = 0.2)
    {
        _lead = leadSeconds;
        WaveFormat = new WaveFormat(sampleRate, 16, channels);
        _ring = new byte[WaveFormat.AverageBytesPerSecond * 2]; // up to 2 s
    }

    public WaveFormat WaveFormat { get; }

    public void Restart(Func<bool> stopWhen)
    {
        lock (_gate) { _read = 0; _count = 0; }
        _stopWhen = stopWhen;
        _bytesDelivered = 0;
        _clock.Restart();
    }

    /// <summary>Takes float stereo from the capture. (Only 48 kHz is passed through; other rates are resampled crudely.)</summary>
    public void AddFloatStereo(ReadOnlySpan<byte> floatBytes, int sourceRate)
    {
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(floatBytes);
        int srcFrames = src.Length / 2;
        int dstFrames = sourceRate == WaveFormat.SampleRate ? srcFrames : (int)((long)srcFrames * WaveFormat.SampleRate / sourceRate);
        lock (_gate)
        {
            for (int f = 0; f < dstFrames; f++)
            {
                int sf = sourceRate == WaveFormat.SampleRate ? f : Math.Min(srcFrames - 1, (int)((long)f * sourceRate / WaveFormat.SampleRate));
                for (int c = 0; c < 2; c++)
                {
                    short s = (short)(Math.Clamp(src[sf * 2 + c], -1f, 1f) * 32767);
                    Push((byte)s);
                    Push((byte)(s >> 8));
                }
            }
        }
    }

    void Push(byte b)
    {
        int write = (_read + _count) % _ring.Length;
        _ring[write] = b;
        if (_count < _ring.Length) _count++;
        else _read = (_read + 1) % _ring.Length; // full: drop oldest
    }

    public int Read(Span<byte> buffer)
    {
        if (_stopWhen()) return 0; // ends the encode
        // The encoder asks for seconds at a time; hand it 20 ms pieces so we never wait long or drop audio.
        int want = Math.Min(buffer.Length, WaveFormat.AverageBytesPerSecond / 50);
        want -= want % WaveFormat.BlockAlign;

        // Stay a little behind real time (200 ms by default), so the captured audio is always already waiting for us.
        while (_bytesDelivered + want > (_clock.Elapsed.TotalSeconds - _lead) * WaveFormat.AverageBytesPerSecond)
        {
            Thread.Sleep(5);
            if (_stopWhen()) return 0;
        }

        lock (_gate)
        {
            // If capture got far ahead (clock drift), skip to keep latency low.
            int maxBacklog = (int)(WaveFormat.AverageBytesPerSecond * Math.Max(0.25, _lead * 2.5)) / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
            if (_count > maxBacklog)
            {
                int drop = (_count - maxBacklog / 2) / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
                _read = (_read + drop) % _ring.Length;
                _count -= drop;
            }
            int take = Math.Min(want, _count / WaveFormat.BlockAlign * WaveFormat.BlockAlign);
            // Not enough audio (silence, or music just started): put the silence first so the
            // audio we do have flows straight into the next chunk without a gap.
            int gap = want - take;
            buffer[..gap].Clear();
            for (int i = 0; i < take; i++) buffer[gap + i] = _ring[(_read + i) % _ring.Length];
            _read = (_read + take) % _ring.Length;
            _count -= take;
        }
        _bytesDelivered += want;
        return want;
    }
}
