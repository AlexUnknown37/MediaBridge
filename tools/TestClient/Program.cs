// MediaBridge test client: pretends to be a game mod so you can try the tool without a game.
//
//   TestClient                      interactive: type commands like  play, next, vol 0.5, devices
//   TestClient get_state next       send each command, print replies, exit
//   TestClient --audio 5            listen to the audio stream for 5 seconds and print the loudness
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

const int ControlPort = 48900;
const int AudioPort = 48901;

if (args.Length >= 1 && args[0] == "--tone")
{
    // --tone "Steam Streaming Speakers" 6   plays a test beep into that output device
    PlayTone(args[1], double.Parse(args[2]));
    return;
}

if (args.Length >= 1 && args[0] == "--radio")
{
    // --radio 8   tunes in to the MP3 radio stream with Windows Media Foundation (what Pacific Drive uses)
    ListenToRadio(args.Length > 1 ? double.Parse(args[1]) : 8);
    return;
}

if (args.Length >= 1 && args[0] == "--audio")
{
    await MeterAudio(args.Length > 1 ? double.Parse(args[1]) : 5);
    return;
}

using var tcp = new TcpClient();
try { await tcp.ConnectAsync("127.0.0.1", ControlPort); }
catch { Console.WriteLine("Could not connect. Is MediaBridge running?"); return; }

var stream = tcp.GetStream();
var reader = new StreamReader(stream, Encoding.UTF8);
var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
JsonArray? lastDevices = null;

_ = Task.Run(async () =>
{
    while (await reader.ReadLineAsync() is { } line)
    {
        var msg = JsonNode.Parse(line)!.AsObject();
        if (msg["devices"] is JsonArray devs)
        {
            lastDevices = devs;
            int i = 0;
            foreach (var d in devs)
                Console.WriteLine($"  [{i++}] {((bool)d!["selected"]! ? "*" : " ")} {d["kind"],-6} {d["name"]}");
            continue;
        }
        if ((string?)msg["mime"] != null)
        {
            var file = Path.Combine(Path.GetTempPath(), "mediabridge_cover" + ((string)msg["mime"]! == "image/jpeg" ? ".jpg" : ".png"));
            File.WriteAllBytes(file, Convert.FromBase64String((string)msg["data"]!));
            Console.WriteLine($"<< cover art saved to {file}");
            continue;
        }
        Console.WriteLine("<< " + line);
    }
    Console.WriteLine("Disconnected.");
});

await Send("""{"cmd":"hello","name":"TestClient"}""");

if (args.Length > 0)
{
    foreach (var a in args) { await Send(Translate(a)); await Task.Delay(400); }
    await Task.Delay(600);
    return;
}

Console.WriteLine("Commands: play pause play_pause next previous | vol 0.5 | up | down | mute | unmute");
Console.WriteLine("          state | devices | select <number> | art | quit   (or type raw JSON)");
while (Console.ReadLine() is { } input && input != "quit")
{
    if (input.Trim().Length == 0) continue;
    await Send(Translate(input.Trim()));
}

string Translate(string input)
{
    var parts = input.Split(' ', 2);
    return parts[0] switch
    {
        _ when input.StartsWith('{') => input,
        "vol" => $$"""{"cmd":"set_volume","value":{{parts[1]}}}""",
        "up" => """{"cmd":"volume_up"}""",
        "down" => """{"cmd":"volume_down"}""",
        "state" => """{"cmd":"get_state"}""",
        "devices" => """{"cmd":"list_devices"}""",
        "art" => """{"cmd":"get_artwork"}""",
        "select" => new JsonObject { ["cmd"] = "select_device", ["deviceId"] = (string?)lastDevices?[int.Parse(parts[1])]?["id"] }.ToJsonString(),
        _ => new JsonObject { ["cmd"] = parts[0] }.ToJsonString(),
    };
}

async Task Send(string json)
{
    Console.WriteLine(">> " + json);
    await writer.WriteLineAsync(json);
}

#pragma warning disable CA1416, CS0618
static void PlayTone(string deviceNamePart, double seconds)
{
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var dev = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
        .First(d => d.FriendlyName.Contains(deviceNamePart, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"Playing a 440 Hz tone (amplitude 0.5) into {dev.FriendlyName} for {seconds} s");
    using var output = new NAudio.Wave.WasapiOut(dev, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 50);
    var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.5 };
    output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(tone));
    output.Play();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
}
static void ListenToRadio(double seconds)
{
    var url = "http://127.0.0.1:48902/MediaBridge.mp3";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var reader = new NAudio.Wave.MediaFoundationReader(url);
    Console.WriteLine($"Opened {url} in {sw.ElapsedMilliseconds} ms: {reader.WaveFormat}");
    var samples = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
    var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 2]; // 0.5 s
    double got = 0;
    int zeroRun = 0, gaps = 0;
    sw.Restart();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        int n = samples.Read(buf.AsSpan());
        if (n == 0) { Console.WriteLine("stream ended"); break; }
        float peak = 0;
        for (int i = 0; i < n; i++)
        {
            peak = Math.Max(peak, Math.Abs(buf[i]));
            if (Math.Abs(buf[i]) < 1e-5f) { if (++zeroRun == reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 200) gaps++; }
            else zeroRun = 0;
        }
        got += n / (double)reader.WaveFormat.Channels / reader.WaveFormat.SampleRate;
        Console.WriteLine($"t={sw.Elapsed.TotalSeconds,5:0.0}s  decoded {got,5:0.0}s  peak {peak:0.000} {new string('#', (int)(peak * 40))}");
    }
    Console.WriteLine($"Silent gaps longer than 5 ms: {gaps}");
}
#pragma warning restore CA1416, CS0618

static async Task MeterAudio(double seconds)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync("127.0.0.1", AudioPort);
    var s = tcp.GetStream();
    var header = new byte[16];
    await s.ReadExactlyAsync(header);
    var magic = Encoding.ASCII.GetString(header, 0, 4);
    int rate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
    int ch = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(8));
    Console.WriteLine($"Audio stream: {magic}, {rate} Hz, {ch} channels, float32");

    var buf = new byte[rate * ch * 4 / 10]; // 100 ms
    var end = DateTime.UtcNow.AddSeconds(seconds);
    long total = 0;
    float peak = 0;
    var nextPrint = DateTime.UtcNow.AddMilliseconds(500);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    try
    {
        while (DateTime.UtcNow < end)
        {
            int n = await s.ReadAsync(buf, cts.Token);
            if (n == 0) break;
            total += n;
            for (int i = 0; i + 4 <= n; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buf, i)));
            if (DateTime.UtcNow >= nextPrint)
            {
                Console.WriteLine($"peak {peak:0.000}  {new string('#', (int)(peak * 40))}");
                peak = 0;
                nextPrint = DateTime.UtcNow.AddMilliseconds(500);
            }
        }
    }
    catch (OperationCanceledException) { }
    Console.WriteLine($"Received {total / 4 / ch / (double)rate:0.00} s of audio in {seconds} s.");
}
