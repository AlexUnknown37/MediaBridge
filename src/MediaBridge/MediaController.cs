using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Windows.Media.Control;
using GsmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace MediaBridge;

/// <summary>
/// Talks to Windows' built-in media controls (the same system behind the volume/media popup).
/// Works with Spotify, browsers, and most music apps.
/// </summary>
sealed class MediaController : IMediaController
{
    GsmtcManager? _manager;
    GsmtcSession? _session;
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    CancellationTokenSource? _debounce;

    readonly object _gate = new();
    JsonObject _state = EmptyState();
    byte[]? _artwork;
    string? _artworkMime;

    /// <summary>Raised (on a background thread) whenever song info or play state changes.</summary>
    public event Action<JsonObject>? Changed;

    public async Task StartAsync()
    {
        _manager = await GsmtcManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => AttachToCurrentSession();
        AttachToCurrentSession();
    }

    /// <summary>A copy of the latest media info.</summary>
    public JsonObject Snapshot()
    {
        lock (_gate) return (JsonObject)_state.DeepClone();
    }

    public (byte[]? Data, string? Mime) Artwork()
    {
        lock (_gate) return (_artwork, _artworkMime);
    }

    public async Task<bool> SendAsync(string command)
    {
        var s = _session;
        if (s == null) return false;
        return command switch
        {
            "play" => await s.TryPlayAsync(),
            "pause" => await s.TryPauseAsync(),
            "play_pause" => await s.TryTogglePlayPauseAsync(),
            "stop" => await s.TryStopAsync(),
            "next" => await s.TrySkipNextAsync(),
            "previous" => await s.TrySkipPreviousAsync(),
            _ => false,
        };
    }

    void AttachToCurrentSession()
    {
        var next = _manager?.GetCurrentSession();
        var old = _session;
        if (old != null && next != null && old.SourceAppUserModelId == next.SourceAppUserModelId) { QueueRefresh(); return; }

        if (old != null)
        {
            old.MediaPropertiesChanged -= OnSessionChanged;
            old.PlaybackInfoChanged -= OnSessionChanged;
            old.TimelinePropertiesChanged -= OnSessionChanged;
        }
        _session = next;
        if (next != null)
        {
            next.MediaPropertiesChanged += OnSessionChanged;
            next.PlaybackInfoChanged += OnSessionChanged;
            next.TimelinePropertiesChanged += OnSessionChanged;
            Log.Write($"Media session: {next.SourceAppUserModelId}");
        }
        else
        {
            Log.Write("Media session: none");
        }
        QueueRefresh();
    }

    void OnSessionChanged(GsmtcSession sender, object args) => QueueRefresh();

    // Apps like Spotify fire several change events in a burst; wait a moment and refresh once.
    void QueueRefresh()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Delay(120, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            await RefreshAsync();
        }, TaskScheduler.Default);
    }

    async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            var s = _session;
            var state = EmptyState();
            byte[]? art = null;
            string? mime = null;

            if (s != null)
            {
                state["app"] = s.SourceAppUserModelId;

                var props = await s.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    state["title"] = props.Title ?? "";
                    state["artist"] = props.Artist ?? "";
                    state["album"] = props.AlbumTitle ?? "";
                    if (props.Thumbnail != null)
                    {
                        try
                        {
                            using var stream = await props.Thumbnail.OpenReadAsync();
                            mime = string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType;
                            using var ms = new MemoryStream();
                            await stream.AsStreamForRead().CopyToAsync(ms);
                            art = ms.ToArray();
                        }
                        catch (Exception ex) { Log.Write($"Artwork read failed: {ex.Message}"); }
                    }
                }

                var playback = s.GetPlaybackInfo();
                state["status"] = playback?.PlaybackStatus switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "changing",
                    _ => "none",
                };
                var controls = playback?.Controls;
                state["canPlay"] = controls?.IsPlayEnabled ?? false;
                state["canPause"] = controls?.IsPauseEnabled ?? false;
                state["canNext"] = controls?.IsNextEnabled ?? false;
                state["canPrevious"] = controls?.IsPreviousEnabled ?? false;

                var timeline = s.GetTimelineProperties();
                if (timeline != null)
                {
                    state["position"] = Math.Round((timeline.Position - timeline.StartTime).TotalSeconds, 3);
                    state["duration"] = Math.Round((timeline.EndTime - timeline.StartTime).TotalSeconds, 3);
                    // Mods can add (now - positionUpdatedAt) to "position" while playing to get a live position.
                    state["positionUpdatedAt"] = timeline.LastUpdatedTime.ToUnixTimeMilliseconds();
                }
            }

            state["artworkId"] = art == null ? null : Convert.ToHexString(SHA256.HashData(art))[..16].ToLowerInvariant();

            lock (_gate)
            {
                _state = state;
                _artwork = art;
                _artworkMime = mime;
            }
            Changed?.Invoke((JsonObject)state.DeepClone());
        }
        catch (Exception ex)
        {
            Log.Write($"Media refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
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
