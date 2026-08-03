using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace HerculesWaveBridge.Tests;

public sealed class BackgroundImageManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hwb-background-tests-{Guid.NewGuid():N}");
    private readonly BridgeLogger _logger = BridgeLogger.CreateDefault();

    [Fact]
    public void ExistingCustomImageMigratesToCustomMode()
    {
        Directory.CreateDirectory(_directory);
        SaveSolidImage(Path.Combine(_directory, "custom-background.png"), 480, 272, Color.Blue);

        var manager = new BackgroundImageManager(_logger, _directory);

        Assert.Equal(BackgroundMode.Custom, manager.Mode);
        Assert.Equal(BackgroundMode.Custom, manager.SpotifyFallbackMode);
    }

    [Fact]
    public void SpotifyRemembersLastNonSpotifyMode()
    {
        var manager = new BackgroundImageManager(_logger, _directory);
        var customSource = Path.Combine(_directory, "source.png");
        SaveSolidImage(customSource, 640, 640, Color.Cyan);
        manager.Import(customSource);
        manager.SetMode(BackgroundMode.Spotify);

        Assert.Equal(BackgroundMode.Spotify, manager.Mode);
        Assert.Equal(BackgroundMode.Custom, manager.SpotifyFallbackMode);

        manager.SetMode(BackgroundMode.BuiltIn);
        manager.SetMode(BackgroundMode.Spotify);

        Assert.Equal(BackgroundMode.BuiltIn, manager.SpotifyFallbackMode);
    }

    [Fact]
    public void SpotifyUnavailableUsesRememberedCustomImage()
    {
        var manager = new BackgroundImageManager(_logger, _directory);
        var customSource = Path.Combine(_directory, "source.png");
        var builtIn = Path.Combine(_directory, "built-in.png");
        SaveSolidImage(customSource, 640, 640, Color.Cyan);
        SaveSolidImage(builtIn, 480, 272, Color.Purple);
        manager.Import(customSource);
        manager.SetMode(BackgroundMode.Spotify);
        manager.SetSpotifyUnavailable("Spotify artwork unavailable");

        Assert.Equal(manager.CustomBackgroundPath, manager.GetActiveBackgroundPath(builtIn));
    }

    [Fact]
    public void SpotifyArtworkIsFullBleedDarkenedAndNativeSized()
    {
        var manager = new BackgroundImageManager(_logger, _directory);
        byte[] source;
        using (var bitmap = new Bitmap(640, 640, PixelFormat.Format24bppRgb))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(200, 100, 50));
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            source = stream.ToArray();
        }

        manager.SetSpotifyArtwork(new SpotifyArtworkSnapshot(
            source,
            "Test Track",
            "Test Artist",
            "Test Album",
            IsPaused: false,
            Fingerprint: "ABC123"));
        manager.SetMode(BackgroundMode.Spotify);

        using var result = new Bitmap(manager.SpotifyBackgroundPath);
        Assert.Equal(480, result.Width);
        Assert.Equal(272, result.Height);
        var pixel = result.GetPixel(240, 136);
        Assert.InRange(pixel.R, 105, 115);
        Assert.InRange(pixel.G, 53, 58);
        Assert.InRange(pixel.B, 26, 30);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveSpotifySessionProvidesArtworkWhenEnabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("HWB_RUN_SPOTIFY_INTEGRATION"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var manager = new BackgroundImageManager(_logger, _directory);
        manager.SetMode(BackgroundMode.Spotify);
        using var service = new SpotifyAlbumArtService(_logger, manager);
        service.Start();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && !File.Exists(manager.SpotifyBackgroundPath))
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(manager.SpotifyBackgroundPath), "Spotify did not publish album artwork through Windows Global Media Controls.");
        using var artwork = new Bitmap(manager.SpotifyBackgroundPath);
        Assert.Equal(480, artwork.Width);
        Assert.Equal(272, artwork.Height);
        Assert.StartsWith("Spotify", manager.CurrentLabel, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Windows image handles can release just after a failed assertion unwinds.
        }
    }

    private static void SaveSolidImage(string path, int width, int height, Color color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        bitmap.Save(path, ImageFormat.Png);
    }
}
