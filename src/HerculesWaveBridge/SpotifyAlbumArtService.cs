using System.Security.Cryptography;
using System.Threading.Channels;
using Windows.Media.Control;

namespace HerculesWaveBridge;

internal sealed record SpotifyArtworkSnapshot(
    byte[] ArtworkBytes,
    string Title,
    string Artist,
    string Album,
    bool IsPaused,
    string Fingerprint)
{
    public string DisplayLabel
    {
        get
        {
            var artist = string.IsNullOrWhiteSpace(Artist) ? "Spotify" : Artist.Trim();
            var title = string.IsNullOrWhiteSpace(Title) ? "Current track" : Title.Trim();
            return IsPaused
                ? $"Spotify paused: {artist} - {title}"
                : $"Spotify: {artist} - {title}";
        }
    }
}

internal sealed class SpotifyAlbumArtService : IDisposable
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MissingGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly BridgeLogger _logger;
    private readonly BackgroundImageManager _backgroundImages;
    private readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _cts = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Task? _worker;
    private bool _disposed;

    public SpotifyAlbumArtService(BridgeLogger logger, BackgroundImageManager backgroundImages)
    {
        _logger = logger;
        _backgroundImages = backgroundImages;
    }

    public void Start()
    {
        if (_worker is { IsCompleted: false })
        {
            return;
        }

        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _refreshRequests.Writer.TryComplete();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Cancellation may race a Windows Runtime callback.
        }

        UnbindSession();
        UnbindManager();
        _cts.Dispose();
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunManagerSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _backgroundImages.SetSpotifyUnavailable("Spotify artwork unavailable");
                _logger.Error(ex, "Spotify media-session listener failed");
                UnbindSession();
                UnbindManager();
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunManagerSessionAsync(CancellationToken cancellationToken)
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;
        _logger.Info("Connected to Windows Global Media Controls for Spotify artwork.");
        RequestRefresh();

        while (await _refreshRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_refreshRequests.Reader.TryRead(out _))
            {
            }

            await Task.Delay(ChangeDebounce, cancellationToken).ConfigureAwait(false);
            while (_refreshRequests.Reader.TryRead(out _))
            {
            }

            await RefreshArtworkAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshArtworkAsync(CancellationToken cancellationToken)
    {
        var spotify = FindSpotifySession();
        if (spotify is null)
        {
            await Task.Delay(MissingGrace, cancellationToken).ConfigureAwait(false);
            spotify = FindSpotifySession();
            if (spotify is null)
            {
                UnbindSession();
                _backgroundImages.SetSpotifyUnavailable("Spotify artwork unavailable");
                return;
            }
        }

        BindSession(spotify);
        var properties = await spotify.TryGetMediaPropertiesAsync();
        if (properties?.Thumbnail is null)
        {
            await Task.Delay(MissingGrace, cancellationToken).ConfigureAwait(false);
            properties = await spotify.TryGetMediaPropertiesAsync();
            if (properties?.Thumbnail is null)
            {
                _backgroundImages.SetSpotifyUnavailable("Spotify artwork unavailable");
                return;
            }
        }

        byte[] artwork;
        using (var randomAccess = await properties.Thumbnail.OpenReadAsync())
        using (var source = randomAccess.AsStreamForRead())
        using (var output = new MemoryStream())
        {
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            artwork = output.ToArray();
        }

        if (artwork.Length == 0)
        {
            _backgroundImages.SetSpotifyUnavailable("Spotify artwork unavailable");
            return;
        }

        var title = properties.Title ?? string.Empty;
        var artist = properties.Artist ?? string.Empty;
        var album = properties.AlbumTitle ?? string.Empty;
        var status = spotify.GetPlaybackInfo()?.PlaybackStatus;
        var isPaused = status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var metadata = System.Text.Encoding.UTF8.GetBytes($"{title}\n{artist}\n{album}\n");
        var fingerprintInput = new byte[metadata.Length + artwork.Length];
        Buffer.BlockCopy(metadata, 0, fingerprintInput, 0, metadata.Length);
        Buffer.BlockCopy(artwork, 0, fingerprintInput, metadata.Length, artwork.Length);
        var fingerprint = Convert.ToHexString(SHA256.HashData(fingerprintInput));

        _backgroundImages.SetSpotifyArtwork(new SpotifyArtworkSnapshot(
            artwork,
            title,
            artist,
            album,
            isPaused,
            fingerprint));
    }

    private GlobalSystemMediaTransportControlsSession? FindSpotifySession()
    {
        if (_manager is null)
        {
            return null;
        }

        return _manager.GetSessions()
            .Where(IsSpotify)
            .OrderByDescending(session =>
                session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            .FirstOrDefault();
    }

    private static bool IsSpotify(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

    private void BindSession(GlobalSystemMediaTransportControlsSession session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        UnbindSession();
        _session = session;
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _logger.Info($"Bound Spotify media session: {_session.SourceAppUserModelId}.");
    }

    private void UnbindSession()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch
        {
            // The Windows session may already have been destroyed.
        }

        _session = null;
    }

    private void UnbindManager()
    {
        if (_manager is null)
        {
            return;
        }

        try
        {
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        catch
        {
            // The Windows session manager may already be shutting down.
        }

        _manager = null;
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) => RequestRefresh();

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => RequestRefresh();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => RequestRefresh();

    private void RequestRefresh() => _refreshRequests.Writer.TryWrite(true);
}
