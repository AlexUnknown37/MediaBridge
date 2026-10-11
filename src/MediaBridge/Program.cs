using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MediaBridge;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() && args.Length > 0)
            return Linux.LinuxSetup.Run(args);

        // Only one MediaBridge may run at a time (they would fight over the ports).
        using var mutex = new Mutex(true, @"Local\MediaBridge.SingleInstance", out bool firstInstance);
        if (!firstInstance) return 0;

        Log.StartNewSession();
        Log.Write($"MediaBridge {BridgeServer.Version} starting on {RuntimeInformation.OSDescription}");

        var settings = Settings.Load();
        if (!OperatingSystem.IsWindows()) Linux.PulseAudioHub.EnsureVirtualSink();
        var (media, audio) = CreatePlatform();
        var server = new BridgeServer(settings, media, audio);
        var radio = new RadioServer(settings, audio) { Commands = server, Media = media };
        var script = new ScriptBridge(media, audio);

        try
        {
            server.Start();
            radio.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            var text = $"MediaBridge could not open port {settings.ControlPort}, {settings.AudioPort} or {settings.RadioPort}.\n" +
                       $"Another program may be using it.\n\n{ex.Message}";
            Log.Write(text);
#if WINDOWS
            Win32.ShowError(text);
#else
            Console.Error.WriteLine(text);
#endif
            return 1;
        }

        audio.SelectInitial(settings.DeviceId);
        if (settings.DeviceId == null && audio.DeviceId != null)
        {
            settings.DeviceId = audio.DeviceId;
            settings.Save();
        }
        if (audio is Linux.PulseAudioHub pulse)
        {
            if (settings.RouteApps.Count > 0) pulse.SetRouteApps(settings.RouteApps);
            if (settings.Listen) pulse.SetListen(true);
        }

        media.StartAsync().ContinueWith(t =>
        {
            if (t.Exception != null) Log.Write($"Media controls unavailable: {t.Exception.InnerException?.Message}");
        });

        script.Start();
        WriteDiscoveryInfo(settings, radio.Url, radio.WavUrl);

#if WINDOWS
        new TrayApp(settings, (MediaController)media, (AudioHub)audio, server, radio).Run();
#else
        Console.WriteLine($"MediaBridge {BridgeServer.Version} running. Control page: http://127.0.0.1:{settings.RadioPort}/  (Ctrl+C to quit)");
        WaitForExitSignal();
#endif

        server.Stop();
        radio.Stop();
        script.Stop();
        audio.Dispose();
        Log.Write("MediaBridge stopped");
        return 0;
    }

    static (IMediaController, IAudioHub) CreatePlatform()
    {
#if WINDOWS
        return (new MediaController(), new AudioHub());
#else
        return (new Linux.MprisMediaController(), new Linux.PulseAudioHub());
#endif
    }

    /// <summary>Linux: run until Ctrl+C or until systemd stops us.</summary>
    static void WaitForExitSignal()
    {
        using var exit = new ManualResetEventSlim();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; exit.Set(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; exit.Set(); });
        exit.Wait();
    }

    /// <summary>
    /// Tells mods where MediaBridge is installed, so they can start it if it isn't running:
    ///   Windows registry: HKEY_CURRENT_USER\Software\MediaBridge  (InstallPath, ControlPort, AudioPort, RadioPort, RadioUrl, Version)
    ///   File: %LOCALAPPDATA%\MediaBridge\bridge.json (Linux: ~/.local/share/MediaBridge and /tmp/MediaBridge)
    /// </summary>
    static void WriteDiscoveryInfo(Settings settings, string radioUrl, string radioWavUrl)
    {
        var exe = Environment.ProcessPath ?? "";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\MediaBridge");
                key.SetValue("InstallPath", exe);
                key.SetValue("ControlPort", settings.ControlPort, RegistryValueKind.DWord);
                key.SetValue("AudioPort", settings.AudioPort, RegistryValueKind.DWord);
                key.SetValue("RadioPort", settings.RadioPort, RegistryValueKind.DWord);
                key.SetValue("RadioUrl", radioUrl);
                key.SetValue("RadioWavUrl", radioWavUrl);
                key.SetValue("Version", BridgeServer.Version);
            }
            catch (Exception ex) { Log.Write($"Could not write registry info: {ex.Message}"); }
        }

        var info = new JsonObject
        {
            ["installPath"] = exe,
            ["controlPort"] = settings.ControlPort,
            ["audioPort"] = settings.AudioPort,
            ["radioPort"] = settings.RadioPort,
            ["radioUrl"] = OperatingSystem.IsWindows() ? radioUrl : radioWavUrl,
            ["radioWavUrl"] = radioWavUrl,
            ["controlPageUrl"] = $"http://127.0.0.1:{settings.RadioPort}/",
            ["version"] = BridgeServer.Version,
            ["protocol"] = BridgeServer.ProtocolVersion,
            ["os"] = OperatingSystem.IsWindows() ? "windows" : "linux",
        }.ToJsonString(new() { WriteIndented = true });

        string[] dirs = OperatingSystem.IsWindows() ? [Settings.DataDir] : [Settings.DataDir, ScriptBridge.LinuxSharedDir];
        foreach (var dir in dirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "bridge.json"), info);
                // A ready-made playlist for games whose radio accepts .m3u8 files (e.g. Pacific Drive).
                // WAV, because Media Foundation players (like Pacific Drive's) start it about twice as fast as MP3.
                File.WriteAllText(Path.Combine(dir, "MediaBridge.m3u8"), radioWavUrl + "\n");
            }
            catch (Exception ex) { Log.Write($"Could not write discovery files to {dir}: {ex.Message}"); }
        }
    }
}
