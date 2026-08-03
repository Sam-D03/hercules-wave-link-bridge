namespace HerculesWaveBridge;

internal sealed class BridgeLogger
{
    private readonly object _lock = new();
    private readonly string _logFile;

    private BridgeLogger(string logDirectory)
    {
        LogDirectory = logDirectory;
        Directory.CreateDirectory(LogDirectory);
        _logFile = Path.Combine(LogDirectory, "bridge.log");
        RotateIfNeeded();
    }

    public string LogDirectory { get; }

    public static BridgeLogger CreateDefault()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerculesWaveBridge",
            "logs");
        return new BridgeLogger(dir);
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(Exception exception, string message) => Write("ERROR", $"{message}: {exception}");

    public string SaveDisplayPreview(Image image)
    {
        lock (_lock)
        {
            var path = Path.Combine(LogDirectory, "last-display-preview.png");
            var tempPath = Path.Combine(LogDirectory, $"last-display-preview.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp");
            image.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
            File.Move(tempPath, path, overwrite: true);
            return path;
        }
    }

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            RotateIfNeeded();
            File.AppendAllText(_logFile, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logFile))
        {
            return;
        }

        var info = new FileInfo(_logFile);
        if (info.Length < 1_000_000)
        {
            return;
        }

        for (var i = 4; i >= 1; i--)
        {
            var source = Path.Combine(LogDirectory, $"bridge.{i}.log");
            var target = Path.Combine(LogDirectory, $"bridge.{i + 1}.log");
            if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: true);
            }
        }

        File.Copy(_logFile, Path.Combine(LogDirectory, "bridge.1.log"), overwrite: true);
        File.Delete(_logFile);
    }
}
