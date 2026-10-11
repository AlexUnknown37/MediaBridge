namespace MediaBridge.Linux;

/// <summary>
/// Linux command-line options:
///   mediabridge --install     start MediaBridge now and every time you log in (Desktop and Game Mode)
///   mediabridge --uninstall   stop it and remove the auto-start
///   mediabridge --status      is it running?
/// It installs as a systemd *user* service, so it needs no root/sudo (works on SteamOS's read-only system).
/// </summary>
static class LinuxSetup
{
    const string Unit = "mediabridge.service";

    static string UnitPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user", Unit);

    public static int Run(string[] args)
    {
        switch (args[0])
        {
            case "--install":
                var exe = Environment.ProcessPath!;
                Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
                File.WriteAllText(UnitPath, $"""
                    [Unit]
                    Description=MediaBridge (music controls and audio for game mods)
                    After=pipewire.service pipewire-pulse.service

                    [Service]
                    ExecStart="{exe}"
                    Restart=on-failure
                    RestartSec=3

                    [Install]
                    WantedBy=default.target

                    """);
                Systemctl("daemon-reload");
                Systemctl("enable", "--now", Unit);
                Console.WriteLine($"""
                    MediaBridge is installed and running. It will start automatically from now on.
                      Control page:  http://127.0.0.1:48902/
                      Service file:  {UnitPath}
                      Uses the program at: {exe}  (don't move it, or run --install again after moving)
                    """);
                return 0;

            case "--uninstall":
                Systemctl("disable", "--now", Unit);
                if (File.Exists(UnitPath)) File.Delete(UnitPath);
                Systemctl("daemon-reload");
                Console.WriteLine("MediaBridge auto-start removed and the service stopped.");
                return 0;

            case "--status":
                Console.WriteLine(Proc.Run("systemctl", "--user", "status", Unit, "--no-pager") ?? "MediaBridge service is not running (or not installed).");
                return 0;

            default:
                Console.WriteLine("Usage: mediabridge [--install | --uninstall | --status]   (no option: run in this terminal)");
                return args[0] is "-h" or "--help" ? 0 : 1;
        }
    }

    static void Systemctl(params string[] args)
    {
        if (Proc.Run("systemctl", ["--user", .. args]) == null)
            Console.Error.WriteLine($"systemctl --user {string.Join(' ', args)} failed (see message above).");
    }
}
