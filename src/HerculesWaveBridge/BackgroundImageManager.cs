using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HerculesWaveBridge;

internal enum BackgroundMode
{
    BuiltIn,
    Custom,
    Spotify
}

internal sealed class BackgroundImageManager
{
    private const int ExifOrientationId = 0x0112;
    private const int SpotifyOverlayAlpha = 115;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly BridgeLogger _logger;
    private readonly string _selectionPath;
    private readonly string _sourceNamePath;
    private BackgroundSelection _selection;
    private bool _spotifyArtworkAvailable;
    private string _spotifyFingerprint = string.Empty;
    private string _spotifyStatus = "Spotify artwork unavailable";

    public BackgroundImageManager(BridgeLogger logger, string? storageDirectory = null)
    {
        _logger = logger;
        StorageDirectory = storageDirectory ?? ResolveStorageDirectory();
        Directory.CreateDirectory(StorageDirectory);

        CustomBackgroundPath = Path.Combine(StorageDirectory, "custom-background.png");
        SpotifyBackgroundPath = Path.Combine(StorageDirectory, "spotify-background.png");
        _sourceNamePath = Path.Combine(StorageDirectory, "custom-background-source.txt");
        _selectionPath = Path.Combine(StorageDirectory, "background-selection.json");
        _selection = LoadSelection();
        SaveSelection(_selection);
    }

    public event EventHandler? ActiveBackgroundChanged;

    public string StorageDirectory { get; }

    public string CustomBackgroundPath { get; }

    public string SpotifyBackgroundPath { get; }

    public bool HasCustomBackground => IsValidBackground(CustomBackgroundPath, logFailure: false);

    public BackgroundMode Mode
    {
        get
        {
            lock (_lock)
            {
                return _selection.Mode;
            }
        }
    }

    public BackgroundMode SpotifyFallbackMode
    {
        get
        {
            lock (_lock)
            {
                return _selection.SpotifyFallbackMode;
            }
        }
    }

    public string CurrentLabel
    {
        get
        {
            BackgroundSelection selection;
            bool spotifyAvailable;
            string spotifyStatus;
            lock (_lock)
            {
                selection = _selection;
                spotifyAvailable = _spotifyArtworkAvailable;
                spotifyStatus = _spotifyStatus;
            }

            return selection.Mode switch
            {
                BackgroundMode.BuiltIn => "Built-in Waves",
                BackgroundMode.Custom => CustomLabel(),
                BackgroundMode.Spotify when spotifyAvailable => spotifyStatus,
                BackgroundMode.Spotify => $"{spotifyStatus}; showing {FallbackLabel(selection.SpotifyFallbackMode)}",
                _ => "Built-in Waves"
            };
        }
    }

    public void SetMode(BackgroundMode mode)
    {
        if (mode == BackgroundMode.Custom && !HasCustomBackground)
        {
            throw new InvalidOperationException("No custom background image has been imported yet.");
        }

        BackgroundSelection next;
        lock (_lock)
        {
            var fallback = mode is BackgroundMode.BuiltIn or BackgroundMode.Custom
                ? mode
                : _selection.SpotifyFallbackMode;
            next = new BackgroundSelection(mode, fallback);
            _selection = next;
            SaveSelection(next);
        }

        _logger.Info($"Display background mode selected: {mode}; Spotify fallback={next.SpotifyFallbackMode}.");
        ActiveBackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Import(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("The selected background image no longer exists.", fullSourcePath);
        }

        try
        {
            using var input = new FileStream(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            WriteTransformedImage(input, CustomBackgroundPath, overlayAlpha: 0);
            File.WriteAllText(_sourceNamePath, Path.GetFileName(fullSourcePath));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "The selected file is not a supported image. Choose a PNG, JPEG, BMP, GIF, or TIFF image.",
                ex);
        }

        _logger.Info(
            $"Imported custom display background from {fullSourcePath}; " +
            $"saved center-cropped {NativeHsmSdk.ScreenWidth}x{NativeHsmSdk.ScreenHeight} PNG to {CustomBackgroundPath}.");
        SetMode(BackgroundMode.Custom);
    }

    public void SetSpotifyArtwork(SpotifyArtworkSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_spotifyArtworkAvailable &&
                string.Equals(_spotifyFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
            {
                _spotifyStatus = snapshot.DisplayLabel;
                return;
            }
        }

        try
        {
            using var input = new MemoryStream(snapshot.ArtworkBytes, writable: false);
            WriteTransformedImage(input, SpotifyBackgroundPath, SpotifyOverlayAlpha);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException)
        {
            _logger.Error(ex, "Could not prepare Spotify album artwork");
            SetSpotifyUnavailable("Spotify artwork unreadable");
            return;
        }

        bool notify;
        lock (_lock)
        {
            _spotifyArtworkAvailable = true;
            _spotifyFingerprint = snapshot.Fingerprint;
            _spotifyStatus = snapshot.DisplayLabel;
            notify = _selection.Mode == BackgroundMode.Spotify;
        }

        _logger.Info($"Prepared Spotify background: {snapshot.Artist} - {snapshot.Title}; fingerprint={snapshot.Fingerprint[..Math.Min(12, snapshot.Fingerprint.Length)]}.");
        if (notify)
        {
            ActiveBackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSpotifyUnavailable(string reason)
    {
        bool notify;
        lock (_lock)
        {
            notify = _spotifyArtworkAvailable && _selection.Mode == BackgroundMode.Spotify;
            _spotifyArtworkAvailable = false;
            _spotifyFingerprint = string.Empty;
            _spotifyStatus = string.IsNullOrWhiteSpace(reason) ? "Spotify artwork unavailable" : reason.Trim();
        }

        if (notify)
        {
            ActiveBackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string GetActiveBackgroundPath(string builtInBackgroundPath)
    {
        BackgroundSelection selection;
        bool spotifyAvailable;
        lock (_lock)
        {
            selection = _selection;
            spotifyAvailable = _spotifyArtworkAvailable;
        }

        return selection.Mode switch
        {
            BackgroundMode.Custom => ResolveCustomOrBuiltIn(builtInBackgroundPath),
            BackgroundMode.Spotify when spotifyAvailable && IsValidBackground(SpotifyBackgroundPath, logFailure: true) => SpotifyBackgroundPath,
            BackgroundMode.Spotify when selection.SpotifyFallbackMode == BackgroundMode.Custom => ResolveCustomOrBuiltIn(builtInBackgroundPath),
            _ => builtInBackgroundPath
        };
    }

    private BackgroundSelection LoadSelection()
    {
        if (!File.Exists(_selectionPath))
        {
            var migrated = HasCustomBackground ? BackgroundMode.Custom : BackgroundMode.BuiltIn;
            return new BackgroundSelection(migrated, migrated);
        }

        try
        {
            var selection = JsonSerializer.Deserialize<BackgroundSelection>(File.ReadAllText(_selectionPath), JsonOptions);
            if (selection is null)
            {
                throw new InvalidDataException("Background selection file was empty.");
            }

            var fallback = selection.SpotifyFallbackMode == BackgroundMode.Spotify
                ? BackgroundMode.BuiltIn
                : selection.SpotifyFallbackMode;
            var mode = selection.Mode == BackgroundMode.Custom && !HasCustomBackground
                ? BackgroundMode.BuiltIn
                : selection.Mode;
            return new BackgroundSelection(mode, fallback);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not read background selection; using the existing image state: {ex.Message}");
            var migrated = HasCustomBackground ? BackgroundMode.Custom : BackgroundMode.BuiltIn;
            return new BackgroundSelection(migrated, migrated);
        }
    }

    private static string ResolveStorageDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HWB_BACKGROUND_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerculesWaveBridge",
            "display");
    }

    private void SaveSelection(BackgroundSelection selection)
    {
        var tempPath = $"{_selectionPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(selection, JsonOptions));
        File.Move(tempPath, _selectionPath, overwrite: true);
    }

    private string ResolveCustomOrBuiltIn(string builtInBackgroundPath) =>
        IsValidBackground(CustomBackgroundPath, logFailure: true)
            ? CustomBackgroundPath
            : builtInBackgroundPath;

    private bool IsValidBackground(string path, bool logFailure)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var image = Image.FromFile(path);
            var valid = image.Width == NativeHsmSdk.ScreenWidth && image.Height == NativeHsmSdk.ScreenHeight;
            if (!valid && logFailure)
            {
                _logger.Warn($"Ignoring background {path} because it is {image.Width}x{image.Height}; expected {NativeHsmSdk.ScreenWidth}x{NativeHsmSdk.ScreenHeight}.");
            }

            return valid;
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                _logger.Warn($"Ignoring unreadable background {path}: {ex.Message}");
            }

            return false;
        }
    }

    private string CustomLabel()
    {
        try
        {
            var sourceName = File.Exists(_sourceNamePath)
                ? File.ReadAllText(_sourceNamePath).Trim()
                : string.Empty;
            return string.IsNullOrWhiteSpace(sourceName)
                ? "Custom Image"
                : $"Custom: {sourceName}";
        }
        catch
        {
            return "Custom Image";
        }
    }

    private string FallbackLabel(BackgroundMode mode) =>
        mode == BackgroundMode.Custom && HasCustomBackground ? CustomLabel() : "Built-in Waves";

    private void WriteTransformedImage(Stream input, string targetPath, int overlayAlpha)
    {
        Directory.CreateDirectory(StorageDirectory);
        var tempPath = Path.Combine(
            StorageDirectory,
            $"{Path.GetFileNameWithoutExtension(targetPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            using var loaded = Image.FromStream(input, useEmbeddedColorManagement: true, validateImageData: true);
            ApplyExifOrientation(loaded);
            using var output = new Bitmap(
                NativeHsmSdk.ScreenWidth,
                NativeHsmSdk.ScreenHeight,
                PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.Clear(Color.Black);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;

                var sourceRectangle = CalculateCoverCrop(
                    loaded.Width,
                    loaded.Height,
                    NativeHsmSdk.ScreenWidth,
                    NativeHsmSdk.ScreenHeight);
                graphics.DrawImage(
                    loaded,
                    new Rectangle(0, 0, NativeHsmSdk.ScreenWidth, NativeHsmSdk.ScreenHeight),
                    sourceRectangle,
                    GraphicsUnit.Pixel);

                if (overlayAlpha > 0)
                {
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.CompositingQuality = CompositingQuality.AssumeLinear;
                    using var overlay = new SolidBrush(Color.FromArgb(overlayAlpha, Color.Black));
                    graphics.FillRectangle(overlay, 0, 0, NativeHsmSdk.ScreenWidth, NativeHsmSdk.ScreenHeight);
                }
            }

            output.Save(tempPath, ImageFormat.Png);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Could not remove temporary background image {tempPath}: {ex.Message}");
            }
        }
    }

    private static RectangleF CalculateCoverCrop(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var sourceAspect = (double)sourceWidth / sourceHeight;
        var targetAspect = (double)targetWidth / targetHeight;
        if (sourceAspect > targetAspect)
        {
            var cropWidth = (float)(sourceHeight * targetAspect);
            return new RectangleF((sourceWidth - cropWidth) / 2f, 0, cropWidth, sourceHeight);
        }

        var cropHeight = (float)(sourceWidth / targetAspect);
        return new RectangleF(0, (sourceHeight - cropHeight) / 2f, sourceWidth, cropHeight);
    }

    private static void ApplyExifOrientation(Image image)
    {
        try
        {
            if (!image.PropertyIdList.Contains(ExifOrientationId))
            {
                return;
            }

            var orientationData = image.GetPropertyItem(ExifOrientationId)?.Value;
            if (orientationData is not { Length: > 0 })
            {
                return;
            }

            image.RotateFlip(orientationData[0] switch
            {
                2 => RotateFlipType.RotateNoneFlipX,
                3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone,
                7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone
            });
        }
        catch
        {
            // Some image codecs expose malformed or inaccessible EXIF metadata.
        }
    }

    private sealed record BackgroundSelection(BackgroundMode Mode, BackgroundMode SpotifyFallbackMode);
}
