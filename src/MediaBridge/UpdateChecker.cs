using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediaBridge;

/// <summary>Checks the latest packaged MediaBridge release on GitHub without blocking startup.</summary>
sealed class UpdateChecker
{
    const string ReleasesApi = "https://api.github.com/repos/AlexUnknown37/MediaBridge/releases/latest";
    public const string ReleasesPage = "https://github.com/AlexUnknown37/MediaBridge/releases";

    static readonly HttpClient Http = CreateHttpClient();
    readonly SemaphoreSlim _checkLock = new(1, 1);
    string? _latestVersion;
    string? _releaseUrl;
    string? _releaseName;

    public event Action<bool>? Changed;

    static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MediaBridge", BridgeServer.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public void Start() => _ = CheckLoopAsync();

    public JsonObject Snapshot()
    {
        lock (this)
            return new JsonObject
            {
                ["currentVersion"] = BridgeServer.Version,
                ["latestVersion"] = _latestVersion,
                ["releaseName"] = _releaseName,
                ["releaseUrl"] = _releaseUrl ?? ReleasesPage,
                ["available"] = IsNewer(_latestVersion),
            };
    }

    bool IsNewer(string? candidate) =>
        Version.TryParse(candidate, out var latest) &&
        Version.TryParse(BridgeServer.Version, out var current) && latest > current;

    async Task CheckLoopAsync()
    {
        while (true)
        {
            try { await CheckNowAsync(); }
            catch (Exception ex) { Log.Write($"GitHub update check failed: {ex.Message}"); }
            await Task.Delay(TimeSpan.FromHours(12));
        }
    }

    async Task CheckNowAsync()
    {
        await _checkLock.WaitAsync();
        try
        {
            using var response = await Http.GetAsync(ReleasesApi);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = json.RootElement;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() == 0)
                return; // A release without a downloadable package is not an update for players.

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var versionText = tag?.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out var parsed)) return;
            versionText = parsed.ToString(3);
            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;

            bool newlyAvailable;
            lock (this)
            {
                newlyAvailable = IsNewer(versionText) && _latestVersion != versionText;
                _latestVersion = versionText;
                _releaseUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "github.com"
                    ? uri.ToString()
                    : ReleasesPage;
                _releaseName = string.IsNullOrWhiteSpace(name) ? $"MediaBridge {versionText}" : name;
            }
            Changed?.Invoke(newlyAvailable);
            Log.Write($"GitHub release check complete: latest packaged version {versionText}");
        }
        finally { _checkLock.Release(); }
    }
}
