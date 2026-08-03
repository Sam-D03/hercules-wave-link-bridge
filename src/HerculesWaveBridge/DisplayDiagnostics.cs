namespace HerculesWaveBridge;

internal static class DisplayDiagnostics
{
    public const string FrameLabelEnvironmentVariable = "HWB_LCD_FRAME_LABEL";

    public static DisplayFrameLabel GetFrameLabel()
    {
        var mode = GetLcdMode();
        var requestedLabel = Environment.GetEnvironmentVariable(FrameLabelEnvironmentVariable);
        var full = string.IsNullOrWhiteSpace(requestedLabel)
            ? GetDefaultLabel(mode)
            : Clean(requestedLabel, 48);

        return new DisplayFrameLabel(full, GetShortLabel(full, mode), mode);
    }

    private static string GetLcdMode()
    {
        var mode = Environment.GetEnvironmentVariable("HWB_LCD_MODE");
        return string.IsNullOrWhiteSpace(mode)
            ? "preview"
            : mode.Trim().ToLowerInvariant().Replace("-", string.Empty);
    }

    private static string GetDefaultLabel(string mode)
    {
        return mode switch
        {
            "helper" => "SDK HELPER LIVE",
            "helperbatch" or "batchedhelper" => "SDK HELPER BATCH LIVE",
            "dynamicraw" or "dynamic" => "DYNAMIC RAW LIVE",
            "preview" or "previewonly" or "none" or "off" or "disabled" or "auto" => "PREVIEW ONLY",
            "raw" => "SAFE RAW RECOVERY",
            _ => "LCD TEST FRAME"
        };
    }

    private static string GetShortLabel(string full, string mode)
    {
        var requestedTag = Environment.GetEnvironmentVariable("HWB_LCD_FRAME_TAG");
        if (!string.IsNullOrWhiteSpace(requestedTag))
        {
            return Clean(requestedTag, 18);
        }

        if (full.Contains("batch", StringComparison.OrdinalIgnoreCase))
        {
            return "BATCH";
        }

        if (full.Contains("helper", StringComparison.OrdinalIgnoreCase))
        {
            return "HELPER";
        }

        if (full.Contains("dynamic", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("dynraw", StringComparison.OrdinalIgnoreCase))
        {
            return "DYNRAW";
        }

        if (full.Contains("recovery", StringComparison.OrdinalIgnoreCase))
        {
            return "RECOVERY";
        }

        return mode switch
        {
            "helper" => "HELPER",
            "helperbatch" or "batchedhelper" => "BATCH",
            "dynamicraw" or "dynamic" => "DYNRAW",
            "preview" or "previewonly" or "none" or "off" or "disabled" or "auto" => "PREVIEW",
            "raw" => "RECOVERY",
            _ => Clean(full, 18)
        };
    }

    private static string Clean(string value, int maxLength)
    {
        var cleaned = new string(value.Trim()
            .Where(ch => ch >= 32 && ch <= 126)
            .ToArray());

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "LCD TEST FRAME";
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
}

internal sealed record DisplayFrameLabel(string Full, string Short, string LcdMode);
