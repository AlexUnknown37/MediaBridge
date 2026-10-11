using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MediaBridge;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Only one MediaBridge may run at a time (they would fight over the ports).
        using var mutex = new Mutex(true, @"Local\MediaBridge.SingleInstance", out bool firstInstance);
        if (!firstInstance) return;

        Log.StartNewSession();
        Log.Write($"MediaBridge {BridgeServer.Version} starting");

        var settings = Settings.Load();
        var media = new MediaController();
        var audio = new AudioHub();
        var server = new BridgeServer(settings, media, audio);
        var radio = new RadioServer(settings, audio);
        var script = new ScriptBridge(media, audio);

        try
        {
            server.Start();
            radio.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Win32.ShowError(
                $"MediaBridge could not open port {settings.ControlPort}, {settings.AudioPort} or {settings.RadioPort}.\n" +
                $"Another program may be using it.\n\n{ex.Message}");
            return;
        }

        audio.SelectInitial(settings.DeviceId);
        if (settings.DeviceId == null && audio.DeviceId != null)
        {
            settings.DeviceId = audio.DeviceId;
            settings.Save();
        }

        media.StartAsync().ContinueWith(t =>
        {
            if (t.Exception != null) Log.Write($"Media controls unavailable: {t.Exception.InnerException?.Message}");
        });

        script.Start();
        WriteDiscoveryInfo(settings, radio.Url);

        new TrayApp(settings, media, audio, server, radio).Run();

        server.Stop();
        radio.Stop();
        script.Stop();
        audio.Dispose();
        Log.Write("MediaBridge stopped");
    }

    /// <summary>
    /// Tells mods where MediaBridge is installed, so they can start it if it isn't running:
    ///   Registry: HKEY_CURRENT_USER\Software\MediaBridge  (InstallPath, ControlPort, AudioPort, RadioPort, RadioUrl, Version)
    ///   File:     %LOCALAPPDATA%\MediaBridge\bridge.json
    /// </summary>
    static void WriteDiscoveryInfo(Settings settings, string radioUrl)
    {
        var exe = Environment.ProcessPath ?? "";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\MediaBridge");
            key.SetValue("InstallPath", exe);
            key.SetValue("ControlPort", settings.ControlPort, RegistryValueKind.DWord);
            key.SetValue("AudioPort", settings.AudioPort, RegistryValueKind.DWord);
            key.SetValue("RadioPort", settings.RadioPort, RegistryValueKind.DWord);
            key.SetValue("RadioUrl", radioUrl);
            key.SetValue("Version", BridgeServer.Version);
        }
        catch (Exception ex) { Log.Write($"Could not write registry info: {ex.Message}"); }

        try
        {
            var info = new JsonObject
            {
                ["installPath"] = exe,
                ["controlPort"] = settings.ControlPort,
                ["audioPort"] = settings.AudioPort,
                ["radioPort"] = settings.RadioPort,
                ["radioUrl"] = radioUrl,
                ["version"] = BridgeServer.Version,
                ["protocol"] = BridgeServer.ProtocolVersion,
            };
            File.WriteAllText(Path.Combine(Settings.DataDir, "bridge.json"), info.ToJsonString(new() { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write($"Could not write bridge.json: {ex.Message}"); }

        // A ready-made playlist for games whose radio accepts .m3u8 files (e.g. Pacific Drive).
        try { File.WriteAllText(Path.Combine(Settings.DataDir, "MediaBridge.m3u8"), radioUrl + "\n"); }
        catch (Exception ex) { Log.Write($"Could not write MediaBridge.m3u8: {ex.Message}"); }
    }
}
