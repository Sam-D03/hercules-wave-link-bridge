using Microsoft.Win32;

namespace HerculesWaveBridge;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hercules Wave Bridge";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value &&
               !string.IsNullOrWhiteSpace(value);
    }

    public static string? CurrentCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            return;
        }

        using var writableKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        writableKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void RefreshRegistrationIfEnabled(BridgeLogger logger)
    {
        if (!IsEnabled())
        {
            return;
        }

        var desired = BuildCommand();
        if (string.Equals(CurrentCommand(), desired, StringComparison.Ordinal))
        {
            return;
        }

        SetEnabled(true);
        logger.Info($"Updated Windows startup registration: {desired}");
    }

    private static string BuildCommand()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = Application.ExecutablePath;
        }

        return $"\"{exePath}\"";
    }
}
