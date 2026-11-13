using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace MediaBridge.Tests;

/// <summary>Talks to real BridgeServer / RadioServer instances over local sockets, with fake media and audio.</summary>
public class ServerTests
{
    sealed class FakeMedia : IMediaController
    {
        public event Action<JsonObject>? Changed;
        public Task StartAsync() => Task.CompletedTask;
        public JsonObject Snapshot() => new() { ["status"] = "playing" };
        public (byte[]? Data, string? Mime) Artwork() => (null, null);
        public Task<bool> SendAsync(string command) => Task.FromResult(true);
    }

    sealed class FakeAudio : IAudioHub
    {
        public string? DeviceId => null;
        public string DeviceName => "fake";
        public int Channels => 0;
        public int SampleRate => 0;
        public event Action<byte[], int>? AudioAvailable;
        public event Action? FormatChanged;
        public event Action<JsonObject>? VolumeChanged;
        public event Action<JsonObject>? DeviceChanged;
        public JsonArray ListDevices() => [];
        public void SelectInitial(string? savedId) { }
        public bool TrySelect(string deviceId) => false;
        public JsonObject VolumeInfo() => [];
        public JsonObject DeviceInfo() => [];
        public bool SetVolume(float value) => true;
        public bool StepVolume(float delta) => true;
        public bool SetMute(bool? muted) => true;
        public void Dispose() { }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static Settings StartServers()
    {
        var settings = new Settings { ControlPort = FreePort(), AudioPort = FreePort(), RadioPort = FreePort() };
        var (media, audio) = (new FakeMedia(), new FakeAudio());
        var server = new BridgeServer(settings, media, audio);
        server.Start();
        new RadioServer(settings, audio) { Commands = server, Media = media }.Start();
        return settings;
    }

    /// <summary>Sends raw bytes and returns everything the server answers until it hangs up (or 2 s pass).</summary>
    static string Exchange(int port, string request)
    {
        using var tcp = new TcpClient("127.0.0.1", port) { ReceiveTimeout = 2000 };
        var stream = tcp.GetStream();
        stream.Write(Encoding.UTF8.GetBytes(request));
        var reply = new MemoryStream();
        var buf = new byte[4096];
        try { for (int n; (n = stream.Read(buf)) > 0;) reply.Write(buf, 0, n); }
        catch (IOException) { } // still open after the timeout: a mod connection stays up
        return Encoding.UTF8.GetString(reply.ToArray());
    }

    static string Page(Settings s, string requestLine, string host, string body = "") =>
        Exchange(s.RadioPort, $"{requestLine} HTTP/1.1\r\nHost: {host}\r\nX-MediaBridge: 1\r\n" +
                              $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

    [Fact]
    public void ControlPort_RunsModCommands_ButNotOnesInsideAWebRequest()
    {
        var s = StartServers();
        Assert.Contains("\"id\":\"mod\",\"ok\":true", Exchange(s.ControlPort, "{\"cmd\":\"ping\",\"id\":\"mod\"}\n"));

        var fromWebPage = Exchange(s.ControlPort,
            "POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: text/plain\r\n\r\n{\"cmd\":\"ping\",\"id\":\"web\"}\n");
        Assert.DoesNotContain("\"id\":\"web\"", fromWebPage);
    }

    [Fact]
    public void ControlPage_KeepsNonEnglishText()
    {
        var s = StartServers();
        var reply = Page(s, "POST /api/cmd", $"127.0.0.1:{s.RadioPort}", "{\"cmd\":\"hello\",\"id\":\"Музыка\"}");
        Assert.Contains("200 OK", reply);
        Assert.Contains("Музыка", JsonNode.Parse(reply[(reply.IndexOf("\r\n\r\n") + 4)..])!["id"]!.GetValue<string>());
    }

    [Fact]
    public void ControlPage_RefusesOtherSiteNames()
    {
        var s = StartServers();
        Assert.Contains("404", Page(s, "POST /api/cmd", "evil.example", "{\"cmd\":\"ping\"}").Split("\r\n")[0]);
        Assert.Contains("404", Page(s, "GET /api/state", "evil.example").Split("\r\n")[0]);
        Assert.Contains("200", Page(s, "GET /api/state", $"localhost:{s.RadioPort}").Split("\r\n")[0]);
    }

    [Fact]
    public void OnlyMp3AndWavAddressesAreTheRadio()
    {
        var s = StartServers();
        Assert.Contains("404", Page(s, "GET /favicon.ico", $"127.0.0.1:{s.RadioPort}").Split("\r\n")[0]);
        Assert.Contains("audio/", Page(s, "HEAD /MediaBridge.wav", $"127.0.0.1:{s.RadioPort}"));
    }

    [Fact]
    public void LivePcm_KeepsTheNewestAudioWhenItsBufferOverflows()
    {
        var pcm = new LivePcmProvider(48000, 2, leadSeconds: 0.01);
        pcm.Restart(stopWhen: () => false);
        // 3 s into a 2 s buffer: 2.5 s of silence, then 0.5 s of sound. The silence must be what's dropped.
        pcm.AddFloatStereo(new byte[48000 * 2 * 4 * 5 / 2], 48000);
        var loud = new float[48000];
        Array.Fill(loud, 0.5f);
        pcm.AddFloatStereo(MemoryMarshal.AsBytes(loud.AsSpan()), 48000);

        Thread.Sleep(60); // Read is paced to real time
        var chunk = new byte[3840];
        Assert.Equal(3840, pcm.Read(chunk));
        Assert.Contains(chunk, b => b != 0);
    }
}
