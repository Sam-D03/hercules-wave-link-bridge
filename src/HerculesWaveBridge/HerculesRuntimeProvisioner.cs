using System.Diagnostics;
using System.Drawing.Imaging;

namespace HerculesWaveBridge;

internal static class HerculesRuntimeProvisioner
{
    public const string DownloadUrl = "https://www.hercules.com/en/stream-control/";

    private static readonly string[] RequiredSdkFiles =
    [
        "hsm_api_core_x64.dll",
        "tlusbapi_x64.dll",
        "tlusbapi_x64.dll.config.ini",
        "tlusbapi_x64.dll.license.ini",
        "HSM01_S32L4R7_v1_38.hsm",
        "HSM01_S32L4R7_v1_42.hsm"
    ];

    public static string? TryFindSdkDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("HWB_HERCULES_SDK_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hercules", "HSM Series", "sdk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Hercules", "HSM Series", "sdk")
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(IsCompleteSdkDirectory);
    }

    public static bool TryProvision(string displayDirectory, BridgeLogger logger, out string error)
    {
        var sourceDirectory = TryFindSdkDirectory();
        if (sourceDirectory is null)
        {
            error = "The Hercules HSM runtime was not found. Install Hercules Stream Control from the official Hercules website first.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(displayDirectory);
            foreach (var fileName in RequiredSdkFiles)
            {
                File.Copy(
                    Path.Combine(sourceDirectory, fileName),
                    Path.Combine(displayDirectory, fileName),
                    overwrite: true);
            }

            logger.Info($"Provisioned the Hercules display runtime from the locally installed SDK: {sourceDirectory}.");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not provision the locally installed Hercules runtime: {ex.Message}";
            logger.Error(ex, "Could not provision the Hercules display runtime");
            return false;
        }
    }

    public static string? TryExtractWaveLinkIcon(string targetPath, BridgeLogger logger)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Contains("WaveLink", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    continue;
                }

                using var icon = Icon.ExtractAssociatedIcon(executablePath);
                if (icon is null)
                {
                    continue;
                }

                using var bitmap = icon.ToBitmap();
                bitmap.Save(targetPath, ImageFormat.Png);
                logger.Info($"Loaded the Wave Link action icon from the locally installed application: {executablePath}.");
                return targetPath;
            }
            catch
            {
                // Some protected processes do not expose their executable path.
            }
            finally
            {
                process.Dispose();
            }
        }

        logger.Warn("Could not extract a Wave Link application icon; action button 4 will use the fallback icon.");
        return null;
    }

    public static void OpenDownloadPage() => Process.Start(new ProcessStartInfo
    {
        FileName = DownloadUrl,
        UseShellExecute = true
    });

    private static bool IsCompleteSdkDirectory(string path) =>
        Directory.Exists(path) && RequiredSdkFiles.All(fileName => File.Exists(Path.Combine(path, fileName)));
}
