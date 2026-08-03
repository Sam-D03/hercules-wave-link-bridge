using System.Runtime.Versioning;
using System.Diagnostics;

namespace HerculesWaveBridge;

internal static class Program
{
    [STAThread]
    [SupportedOSPlatform("windows")]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var logger = BridgeLogger.CreateDefault();
        using var singleInstance = new Mutex(true, @"Local\HerculesWaveBridge", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Hercules Wave Bridge is already running. Check the tray icon.",
                "Hercules Wave Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, args) => logger.Error(args.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                logger.Error(ex, "Unhandled app exception");
            }
        };

        if (!EnsureOfficialHerculesAppClosed(logger))
        {
            return;
        }

        if (HerculesRuntimeProvisioner.TryFindSdkDirectory() is null)
        {
            var openDownload = MessageBox.Show(
                "Hercules Wave Bridge needs the driver and display runtime installed by Hercules Stream Control.\n\nInstall the official Hercules software, restart Windows, then run the bridge again. Open the Hercules download page now?",
                "Hercules Runtime Required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (openDownload == DialogResult.Yes)
            {
                HerculesRuntimeProvisioner.OpenDownloadPage();
            }

            return;
        }

        StartupManager.RefreshRegistrationIfEnabled(logger);
        var atlasSettings = new AtlasLiveSettingsStore(logger);
        var backgroundImages = new BackgroundImageManager(logger);
        using var controller = new BridgeController(logger, atlasSettings);
        using var displayProcess = new AtlasLiveDisplayProcess(logger, atlasSettings, controller, backgroundImages);
        using var spotifyArtwork = new SpotifyAlbumArtService(logger, backgroundImages);
        using var context = new TrayAppContext(controller, logger, atlasSettings, displayProcess, backgroundImages);
        backgroundImages.ActiveBackgroundChanged += (_, _) => displayProcess.Refresh();
        displayProcess.Start();
        spotifyArtwork.Start();
        controller.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Application.Run(context);
    }

    private static bool EnsureOfficialHerculesAppClosed(BridgeLogger logger)
    {
        var processes = Process.GetProcessesByName("Stream-control");
        if (processes.Length == 0)
        {
            return true;
        }

        var close = MessageBox.Show(
            "Hercules Stream Control is currently running and cannot share the Stream 100 display. Close it and continue?",
            "Close Hercules Stream Control",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (close != DialogResult.Yes)
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return false;
        }

        foreach (var process in processes)
        {
            try
            {
                logger.Info($"Closing official Hercules Stream Control process: pid={process.Id}.");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                logger.Warn($"Could not close official Hercules Stream Control process {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        var remaining = Process.GetProcessesByName("Stream-control");
        var closed = remaining.Length == 0;
        foreach (var process in remaining)
        {
            process.Dispose();
        }

        return closed;
    }
}
