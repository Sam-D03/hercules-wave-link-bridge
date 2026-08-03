using System.Text.Json;

namespace HerculesWaveBridge;

internal sealed record AtlasLiveSettings(
    string MeterMode,
    string Shape,
    string ColorMode,
    string SolidColor,
    string GradientStart,
    string GradientEnd,
    double VuGain,
    double VuRefresh,
    int KnobSensitivityLevel,
    string? MeterOverride0,
    string? MeterOverride1,
    string? MeterOverride2,
    string? MeterOverride3,
    string? EncoderTarget0,
    string? EncoderTarget1,
    string? EncoderTarget2,
    string? EncoderTarget3)
{
    public const string ChannelEncoderTarget = "channel";
    public const string PersonalMixOutput1EncoderTarget = "personal-mix-output-1";

    public static AtlasLiveSettings Default { get; } = new(
        "stereo",
        "bars",
        "gradient",
        "#ff5c00",
        "#ff5c00",
        "#80ff00",
        1.16,
        0.04,
        1,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        ChannelEncoderTarget,
        ChannelEncoderTarget,
        ChannelEncoderTarget,
        ChannelEncoderTarget);

    public string MeterOverrideFor(int index) => index switch
    {
        0 => MeterOverride0 ?? string.Empty,
        1 => MeterOverride1 ?? string.Empty,
        2 => MeterOverride2 ?? string.Empty,
        3 => MeterOverride3 ?? string.Empty,
        _ => string.Empty
    };

    public AtlasLiveSettings WithMeterOverride(int index, string? matchText)
    {
        var normalized = NormalizeOverride(matchText);
        return index switch
        {
            0 => this with { MeterOverride0 = normalized },
            1 => this with { MeterOverride1 = normalized },
            2 => this with { MeterOverride2 = normalized },
            3 => this with { MeterOverride3 = normalized },
            _ => this
        };
    }

    public string EncoderTargetFor(int index) => NormalizeEncoderTarget(index switch
    {
        0 => EncoderTarget0,
        1 => EncoderTarget1,
        2 => EncoderTarget2,
        3 => EncoderTarget3,
        _ => ChannelEncoderTarget
    });

    public AtlasLiveSettings WithEncoderTarget(int index, string? target)
    {
        var normalized = NormalizeEncoderTarget(target);
        return index switch
        {
            0 => this with { EncoderTarget0 = normalized },
            1 => this with { EncoderTarget1 = normalized },
            2 => this with { EncoderTarget2 = normalized },
            3 => this with { EncoderTarget3 = normalized },
            _ => this
        };
    }

    public static string NormalizeOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    public static string NormalizeEncoderTarget(string? value) =>
        string.Equals(value, PersonalMixOutput1EncoderTarget, StringComparison.OrdinalIgnoreCase)
            ? PersonalMixOutput1EncoderTarget
            : ChannelEncoderTarget;
}

internal sealed class AtlasLiveSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _lock = new();

    public AtlasLiveSettingsStore(BridgeLogger logger)
    {
        Logger = logger;
        SettingsPath = ResolveSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        Current = LoadOrDefault();
        Save(Current);
    }

    public event EventHandler? SettingsChanged;

    public BridgeLogger Logger { get; }

    public string SettingsPath { get; }

    public AtlasLiveSettings Current { get; private set; }

    public void Update(Func<AtlasLiveSettings, AtlasLiveSettings> update)
    {
        AtlasLiveSettings next;
        lock (_lock)
        {
            next = Normalize(update(Current));
            if (next == Current)
            {
                return;
            }

            Current = next;
            Save(next);
        }

        Logger.Info($"Atlas live settings updated: mode={next.MeterMode}, shape={next.Shape}, color={next.ColorMode}, solid={next.SolidColor}, gradient={next.GradientStart}->{next.GradientEnd}, vuGain={next.VuGain:0.##}x, vuRefresh={next.VuRefresh:0.###}s, knobSensitivity={next.KnobSensitivityLevel}, meterOverrides=[{next.MeterOverrideFor(0)}, {next.MeterOverrideFor(1)}, {next.MeterOverrideFor(2)}, {next.MeterOverrideFor(3)}], encoderTargets=[{next.EncoderTargetFor(0)}, {next.EncoderTargetFor(1)}, {next.EncoderTargetFor(2)}, {next.EncoderTargetFor(3)}].");
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private AtlasLiveSettings LoadOrDefault()
    {
        if (!File.Exists(SettingsPath))
        {
            return AtlasLiveSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AtlasLiveSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            return Normalize(settings ?? AtlasLiveSettings.Default);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to read atlas live settings from {SettingsPath}; using defaults");
            return AtlasLiveSettings.Default;
        }
    }

    private void Save(AtlasLiveSettings settings)
    {
        var tempPath = $"{SettingsPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    private static string ResolveSettingsPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HWB_ATLAS_SETTINGS");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerculesWaveBridge",
            "atlas-live-settings.json");
    }

    private static AtlasLiveSettings Normalize(AtlasLiveSettings settings)
    {
        var defaults = AtlasLiveSettings.Default;
        var meterMode = IsOneOf(settings.MeterMode, "mono", "stereo") ? settings.MeterMode : defaults.MeterMode;
        var shape = IsOneOf(settings.Shape, "bars", "blocks", "pillars", "thin") ? settings.Shape : defaults.Shape;
        var colorMode = IsOneOf(settings.ColorMode, "solid", "gradient") ? settings.ColorMode : defaults.ColorMode;
        var solid = NormalizeColor(settings.SolidColor, defaults.SolidColor);
        var start = NormalizeColor(settings.GradientStart, defaults.GradientStart);
        var end = NormalizeColor(settings.GradientEnd, defaults.GradientEnd);
        var gain = settings.VuGain is >= 0.50 and <= 2.00 ? settings.VuGain : defaults.VuGain;
        var refresh = settings.VuRefresh is >= 0.01 and <= 0.2 ? settings.VuRefresh : defaults.VuRefresh;
        var knobSensitivity = settings.KnobSensitivityLevel is >= 1 and <= 5 ? settings.KnobSensitivityLevel : defaults.KnobSensitivityLevel;
        return new AtlasLiveSettings(
            meterMode,
            shape,
            colorMode,
            solid,
            start,
            end,
            gain,
            refresh,
            knobSensitivity,
            AtlasLiveSettings.NormalizeOverride(settings.MeterOverride0),
            AtlasLiveSettings.NormalizeOverride(settings.MeterOverride1),
            AtlasLiveSettings.NormalizeOverride(settings.MeterOverride2),
            AtlasLiveSettings.NormalizeOverride(settings.MeterOverride3),
            AtlasLiveSettings.NormalizeEncoderTarget(settings.EncoderTarget0),
            AtlasLiveSettings.NormalizeEncoderTarget(settings.EncoderTarget1),
            AtlasLiveSettings.NormalizeEncoderTarget(settings.EncoderTarget2),
            AtlasLiveSettings.NormalizeEncoderTarget(settings.EncoderTarget3));
    }

    private static bool IsOneOf(string? value, params string[] allowed)
    {
        return value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (value is null || value.Length != 7 || value[0] != '#')
        {
            return fallback;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return fallback;
            }
        }

        return value.ToLowerInvariant();
    }
}
