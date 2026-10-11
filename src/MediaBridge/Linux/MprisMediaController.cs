using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MediaBridge.Linux;

/// <summary>
/// Linux media controls through MPRIS, the standard every Linux music app speaks over D-Bus
/// (Spotify, Firefox, Chrome, VLC, ...). Uses the `busctl` tool that ships with systemd (SteamOS included).
///
/// MPRIS players can be asked for their state at any time, so we simply check once a second;
/// that's plenty for song changes and keeps this file free of D-Bus libraries.
/// </summary>
sealed class MprisMediaController : IMediaController
{
    const string Prefix = "org.mpris.MediaPlayer2.";
    const string ObjPath = "/org/mpris/MediaPlayer2";
    const string PlayerIface = "org.mpris.MediaPlayer2.Player";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly object _gate = new();
    JsonObject _state = EmptyState();
    string _lastFingerprint = "";
    byte[]? _artwork;
    string? _artworkMime;
    string? _artUrl;
    string? _player;             // e.g. org.mpris.MediaPlayer2.spotify
    readonly CancellationTokenSource _stop = new();

    public event Action<JsonObject>? Changed;

    public Task StartAsync()
    {
        if (Proc.Run("busctl", "--version") == null)
            throw new InvalidOperationException("busctl was not found; media controls need systemd's busctl.");
        _ = Task.Run(PollLoop);
        return Task.CompletedTask;
    }

    public JsonObject Snapshot() { lock (_gate) return (JsonObject)_state.DeepClone(); }
    public (byte[]? Data, string? Mime) Artwork() { lock (_gate) return (_artwork, _artworkMime); }

    public async Task<bool> SendAsync(string command)
    {
        var player = _player ?? PickPlayer();
        if (player == null) return false;
        var method = command switch
        {
            "play" => "Play",
            "pause" => "Pause",
            "play_pause" => "PlayPause",
            "stop" => "Stop",
            "next" => "Next",
            "previous" => "Previous",
            _ => null,
        };
        if (method == null) return false;
        var ok = await Task.Run(() => Proc.Run("busctl", "--user", "call", player, ObjPath, PlayerIface, method) != null);
        _ = Task.Delay(300).ContinueWith(_ => Refresh()); // show the result quickly
        return ok;
    }

    async Task PollLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { Refresh(); }
            catch (Exception ex) { Log.Write($"MPRIS refresh failed: {ex.Message}"); }
            await Task.Delay(1000);
        }
    }

    /// <summary>The player to follow: one that's playing (Spotify first), else the one we had, else any.</summary>
    string? PickPlayer()
    {
        var json = Busctl("--user", "call", "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames");
        var names = json?["data"]?[0]?.AsArray().Select(n => (string?)n).Where(n => n != null && n.StartsWith(Prefix)).ToList();
        if (names == null || names.Count == 0) return null;

        var playing = names.Where(n => GetString(n!, "PlaybackStatus") == "Playing").ToList();
        return playing.FirstOrDefault(n => n!.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            ?? playing.FirstOrDefault()
            ?? (names.Contains(_player) ? _player : null)
            ?? names.FirstOrDefault(n => n!.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            ?? names[0];
    }

    void Refresh()
    {
        var player = PickPlayer();
        if (player != _player) Log.Write($"Media player: {player ?? "none"}");
        _player = player;

        var state = EmptyState();
        string? artUrl = null;
        if (player != null)
        {
            state["app"] = player[Prefix.Length..];
            var p = GetProps(player, "Metadata", "PlaybackStatus", "Position", "CanControl", "CanPlay", "CanPause", "CanGoNext", "CanGoPrevious");
            var meta = p.GetValueOrDefault("Metadata")?.AsObject();
            if (meta != null)
            {
                state["title"] = (string?)meta["xesam:title"]?["data"] ?? "";
                state["artist"] = string.Join(", ", meta["xesam:artist"]?["data"]?.AsArray().Select(a => (string?)a) ?? []);
                state["album"] = (string?)meta["xesam:album"]?["data"] ?? "";
                var lengthUs = meta["mpris:length"]?["data"];
                if (lengthUs != null) state["duration"] = Math.Round((double)lengthUs / 1_000_000, 3);
                artUrl = (string?)meta["mpris:artUrl"]?["data"];
            }
            state["status"] = (string?)p.GetValueOrDefault("PlaybackStatus") switch
            {
                "Playing" => "playing",
                "Paused" => "paused",
                "Stopped" => "stopped",
                _ => "none",
            };
            bool Flag(string name) => (bool?)p.GetValueOrDefault(name) ?? true;
            bool canControl = Flag("CanControl");
            state["canPlay"] = canControl && Flag("CanPlay") && (string?)state["status"] != "playing";
            state["canPause"] = canControl && Flag("CanPause") && (string?)state["status"] == "playing";
            state["canNext"] = canControl && Flag("CanGoNext");
            state["canPrevious"] = canControl && Flag("CanGoPrevious");
            var posUs = p.GetValueOrDefault("Position");
            if (posUs != null) state["position"] = Math.Round((double)posUs / 1_000_000, 3);
            state["positionUpdatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        if (artUrl != _artUrl) LoadArtwork(artUrl);
        lock (_gate) state["artworkId"] = _artwork == null ? null : Convert.ToHexString(SHA256.HashData(_artwork))[..16].ToLowerInvariant();

        // Only announce real changes (position moves every second while playing; mods compute that themselves).
        var fingerprint = string.Join('|', state["app"], state["title"], state["artist"], state["album"], state["status"],
            state["duration"], state["artworkId"], state["canNext"], state["canPrevious"]);
        lock (_gate) _state = state;
        if (fingerprint == _lastFingerprint) return;
        _lastFingerprint = fingerprint;
        Changed?.Invoke((JsonObject)state.DeepClone());
    }

    void LoadArtwork(string? url)
    {
        _artUrl = url;
        byte[]? data = null;
        string? mime = null;
        try
        {
            if (url != null && url.StartsWith("file://"))
            {
                data = File.ReadAllBytes(new Uri(url).LocalPath);
            }
            else if (url != null && (url.StartsWith("https://") || url.StartsWith("http://")))
            {
                using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                {
                    data = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    mime = resp.Content.Headers.ContentType?.MediaType;
                }
            }
        }
        catch (Exception ex) { Log.Write($"Artwork download failed: {ex.Message}"); }
        if (data != null) mime ??= data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 ? "image/jpeg" : "image/png";
        lock (_gate) { _artwork = data; _artworkMime = mime; }
    }

    string? GetString(string player, string prop) =>
        (string?)Busctl("--user", "get-property", player, ObjPath, PlayerIface, prop)?["data"];

    /// <summary>
    /// Reads several player properties with one busctl call (one JSON line each). If the player doesn't
    /// support one of them the whole call fails, so then we ask one at a time.
    /// </summary>
    static Dictionary<string, JsonNode?> GetProps(string player, params string[] props)
    {
        var result = new Dictionary<string, JsonNode?>();
        var output = Proc.Run("busctl", ["--json=short", "--user", "get-property", player, ObjPath, PlayerIface, .. props]);
        var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines != null && lines.Length == props.Length)
        {
            for (int i = 0; i < props.Length; i++)
            {
                try { result[props[i]] = JsonNode.Parse(lines[i])?["data"]; } catch { result[props[i]] = null; }
            }
            return result;
        }
        foreach (var prop in props)
            result[prop] = Busctl("--user", "get-property", player, ObjPath, PlayerIface, prop)?["data"];
        return result;
    }

    /// <summary>busctl with JSON output, e.g. {"type":"s","data":"Playing"}.</summary>
    static JsonNode? Busctl(params string[] args)
    {
        var output = Proc.Run("busctl", ["--json=short", .. args]);
        if (string.IsNullOrWhiteSpace(output)) return null;
        try { return JsonNode.Parse(output); } catch { return null; }
    }

    static JsonObject EmptyState() => new()
    {
        ["app"] = null,
        ["title"] = "",
        ["artist"] = "",
        ["album"] = "",
        ["status"] = "none",
        ["canPlay"] = false,
        ["canPause"] = false,
        ["canNext"] = false,
        ["canPrevious"] = false,
        ["position"] = 0.0,
        ["duration"] = 0.0,
        ["positionUpdatedAt"] = 0L,
        ["artworkId"] = null,
    };
}
