using System.Text.Json.Nodes;

namespace MediaBridge;

/// <summary>
/// User settings, saved as JSON in %LOCALAPPDATA%\MediaBridge\settings.json.
/// </summary>
sealed class Settings
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaBridge");

    static readonly string FilePath = Path.Combine(DataDir, "settings.json");

    /// <summary>Windows device ID of the audio device the user picked. Null = auto (prefers VB-Cable).</summary>
    public string? DeviceId { get; set; }

    public int ControlPort { get; set; } = 48900;
    public int AudioPort { get; set; } = 48901;
    public int RadioPort { get; set; } = 48902;

    /// <summary>How much volume_up / volume_down change the volume (0.05 = 5%).</summary>
    public float VolumeStep { get; set; } = 0.05f;

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            var json = JsonNode.Parse(File.ReadAllText(FilePath))?.AsObject();
            if (json == null) return s;
            s.DeviceId = (string?)json["deviceId"];
            s.ControlPort = (int?)json["controlPort"] ?? s.ControlPort;
            s.AudioPort = (int?)json["audioPort"] ?? s.AudioPort;
            s.RadioPort = (int?)json["radioPort"] ?? s.RadioPort;
            s.VolumeStep = (float?)json["volumeStep"] ?? s.VolumeStep;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read settings, using defaults: {ex.Message}");
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = new JsonObject
            {
                ["deviceId"] = DeviceId,
                ["controlPort"] = ControlPort,
                ["audioPort"] = AudioPort,
                ["radioPort"] = RadioPort,
                ["volumeStep"] = VolumeStep,
            };
            File.WriteAllText(FilePath, json.ToJsonString(new() { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save settings: {ex.Message}");
        }
    }
}

static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(Settings.DataDir, "log.txt");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never crash the tool */ }
        }
    }

    public static void StartNewSession()
    {
        try
        {
            // Keep the log small: start fresh each launch, keep the previous one.
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".old", true);
            File.Delete(FilePath);
        }
        catch { }
    }
}
