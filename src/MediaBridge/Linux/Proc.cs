using System.Diagnostics;

namespace MediaBridge.Linux;

/// <summary>Runs the small command-line tools that ship with Linux desktops (busctl, pactl, pacat).</summary>
static class Proc
{
    /// <summary>Runs a command and returns its output, or null if it failed or isn't installed.</summary>
    public static string? Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            if (p.ExitCode != 0)
            {
                var err = stderr.Result.Trim();
                if (err.Length > 0) Log.Write($"{file} {string.Join(' ', args)}: {err}");
                return null;
            }
            return stdout.Result;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // not installed
        }
    }

    /// <summary>Starts a long-running command whose output we read as it comes (e.g. audio, event streams).</summary>
    public static Process? Start(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p != null) _ = p.StandardError.ReadToEndAsync(); // drain so it never blocks
            return p;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static bool Exists(string file) => Run("sh", "-c", $"command -v {file}") != null;
}
