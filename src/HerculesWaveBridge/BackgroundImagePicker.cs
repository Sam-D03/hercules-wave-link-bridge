using System.Diagnostics;
using System.Reflection;

namespace HerculesWaveBridge;

internal static class BackgroundImagePicker
{
    private const string PickerResourceName = "BackgroundPicker.HerculesWaveBridge.FilePicker.exe";
    private const string PickerFileName = "HerculesWaveBridge.FilePicker.exe";

    public static string? Choose()
    {
        var resultPath = Path.Combine(
            Path.GetTempPath(),
            $"HerculesWaveBridge-background-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        var errorPath = $"{resultPath}.error";
        try
        {
            var executablePath = ExtractPicker();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    resultPath
                }
            }) ?? throw new InvalidOperationException("Windows did not start the isolated background picker.");

            process.WaitForExit();
            if (File.Exists(errorPath))
            {
                throw new InvalidOperationException(
                    "The Windows image picker failed.",
                    new InvalidOperationException(File.ReadAllText(errorPath)));
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The Windows image picker exited unexpectedly with code {process.ExitCode}.");
            }

            if (!File.Exists(resultPath))
            {
                return null;
            }

            var selectedPath = File.ReadAllText(resultPath).Trim();
            return string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath;
        }
        finally
        {
            TryDelete(resultPath);
            TryDelete(errorPath);
        }
    }

    private static string ExtractPicker()
    {
        var pickerDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerculesWaveBridge",
            "picker");
        Directory.CreateDirectory(pickerDirectory);
        var pickerPath = Path.Combine(pickerDirectory, PickerFileName);

        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(PickerResourceName)
            ?? throw new InvalidOperationException($"Embedded background picker not found: {PickerResourceName}");
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        var embeddedBytes = memory.ToArray();

        if (File.Exists(pickerPath))
        {
            var existingBytes = File.ReadAllBytes(pickerPath);
            if (existingBytes.AsSpan().SequenceEqual(embeddedBytes))
            {
                return pickerPath;
            }
        }

        var tempPath = $"{pickerPath}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tempPath, embeddedBytes);
        File.Move(tempPath, pickerPath, overwrite: true);
        return pickerPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary picker files can be reclaimed by Windows later.
        }
    }
}
