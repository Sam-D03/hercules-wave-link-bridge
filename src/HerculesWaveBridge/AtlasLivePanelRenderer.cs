using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HerculesWaveBridge;

internal sealed class AtlasLivePanelRenderer
{
    public const int IconWidth = 32;
    public const int IconHeight = 32;
    public const uint BottomTargetBase = 0x80000000;

    private static readonly Color[] Accents =
    [
        Color.FromArgb(65, 196, 226),
        Color.FromArgb(178, 112, 255),
        Color.FromArgb(102, 220, 143),
        Color.FromArgb(255, 207, 86)
    ];

    private static readonly Dictionary<string, int> VuShapeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bars"] = 1,
        ["blocks"] = 2,
        ["pillars"] = 3,
        ["thin"] = 4
    };

    private static readonly Dictionary<string, int> VuModeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mono"] = 0,
        ["stereo"] = 1
    };

    private static readonly byte[][] OfficialVuChannelStyles =
    [
        Convert.FromHexString("c8000000010000000100000001000000232323000200000000ff80ff005cffff000000000000000000000000000000000000c842000000000000000000000000000000000000f03f00000000000000000000000000000000010000000000ffff01010000ffffff002323230002000000808080ff808080ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000000ffff01010000ffffff00000000000000c842"),
        Convert.FromHexString("c8000000010000000100000001000000232323000200000035dfffff35dfffff000000000000000000000000000000000000c842000000000000000000000000000000000000f03f00000000000000000000000000000000010000000000ffff01010000ffffff002323230002000000808080ff808080ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000000ffff01010000ffffff00000000000000c842"),
        Convert.FromHexString("c80000000100000001000000010000002323230002000000424247ff424247ff000000000000000000000000000000000000c842000000000000000000000000000000000000f03f00000000000000000000000000000000010000000000ffff01010000ffffff002323230002000000808080ff808080ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000000ffff01010000ffffff00000000000000c842"),
        Convert.FromHexString("c80000000100000001000000010000002323230002000000d33002ffe3ff8bff000000000000000000000000000000000000c842000000000000000000000000000000000000f03f00000000000000000000000000000000010000000000ffff01010000ffffff002323230002000000808080ff808080ff0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000000ffff01010000ffffff00000000000000c842")
    ];

    private string _backgroundPath;
    private readonly string? _waveLinkIconPath;
    private readonly Dictionary<string, byte[]> _iconCache = new(StringComparer.Ordinal);
    private byte[]? _backgroundPixels;
    private byte[]? _waveLinkPixels;

    public AtlasLivePanelRenderer(string backgroundPath, string? waveLinkIconPath)
    {
        _backgroundPath = backgroundPath;
        _waveLinkIconPath = waveLinkIconPath;
    }

    public byte[] RenderBackground()
    {
        if (_backgroundPixels is not null)
        {
            return _backgroundPixels;
        }

        using var source = Image.FromFile(_backgroundPath);
        if (source.Width != NativeHsmSdk.ScreenWidth || source.Height != NativeHsmSdk.ScreenHeight)
        {
            throw new InvalidOperationException($"Background image must be {NativeHsmSdk.ScreenWidth}x{NativeHsmSdk.ScreenHeight}; got {source.Width}x{source.Height}.");
        }

        using var bitmap = new Bitmap(NativeHsmSdk.ScreenWidth, NativeHsmSdk.ScreenHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        _backgroundPixels = ToBgra(bitmap);
        return _backgroundPixels;
    }

    public void ReloadBackground(string backgroundPath)
    {
        _backgroundPath = backgroundPath;
        _backgroundPixels = null;
    }

    public byte[] RenderTopStrip(ChannelSlot slot)
    {
        using var bitmap = new Bitmap(NativeHsmSdk.StripWidth, NativeHsmSdk.StripHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        if (!slot.IsActive)
        {
            return ToBgra(bitmap);
        }

        using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(slot.IsMuted ? Color.FromArgb(235, 78, 78) : Color.White);
        var text = FitText(graphics, slot.ChannelName, font, NativeHsmSdk.StripWidth - 4);
        DrawCenteredText(graphics, new RectangleF(2, 0, NativeHsmSdk.StripWidth - 4, NativeHsmSdk.StripHeight), text, font, brush);
        return ToBgra(bitmap);
    }

    public byte[] RenderChannelIcon(ChannelSlot slot)
    {
        if (slot.ChannelType.Equals("Output", StringComparison.OrdinalIgnoreCase) ||
            slot.ChannelType.Equals("Mix", StringComparison.OrdinalIgnoreCase))
        {
            _waveLinkPixels ??= TryRenderImageIcon(_waveLinkIconPath);
            if (_waveLinkPixels is not null)
            {
                return _waveLinkPixels;
            }
        }

        if (!string.IsNullOrWhiteSpace(slot.IconData))
        {
            var key = slot.IconData;
            if (_iconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var decoded = TryRenderWaveLinkIcon(slot.IconData);
            if (decoded is not null)
            {
                if (_iconCache.Count > 128)
                {
                    _iconCache.Clear();
                }

                _iconCache[key] = decoded;
                return decoded;
            }
        }

        return RenderFallbackIcon(slot);
    }

    public byte[] RenderActionStrip(bool active)
    {
        using var bitmap = new Bitmap(NativeHsmSdk.StripWidth, NativeHsmSdk.StripHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(active ? Color.FromArgb(12, 14, 18) : Color.FromArgb(8, 10, 13));
        return ToBgra(bitmap);
    }

    public byte[] RenderActionIcon(int index)
    {
        if (index == 3)
        {
            _waveLinkPixels ??= TryRenderImageIcon(_waveLinkIconPath);
            if (_waveLinkPixels is not null)
            {
                return _waveLinkPixels;
            }
        }

        using var bitmap = new Bitmap(IconWidth, IconHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(245, 255, 255, 255), 3);
        using var brush = new SolidBrush(pen.Color);
        graphics.DrawEllipse(pen, 3, 3, 25, 25);
        switch (index)
        {
            case 0:
                graphics.FillPolygon(brush, [new Point(11, 16), new Point(21, 9), new Point(21, 23)]);
                graphics.FillRectangle(brush, 8, 9, 2, 14);
                break;
            case 1:
                graphics.FillPolygon(brush, [new Point(12, 9), new Point(23, 16), new Point(12, 23)]);
                break;
            case 2:
                graphics.FillPolygon(brush, [new Point(21, 16), new Point(11, 9), new Point(11, 23)]);
                graphics.FillRectangle(brush, 22, 9, 2, 14);
                break;
            default:
                graphics.DrawLine(pen, 10, 16, 22, 16);
                break;
        }

        return ToBgra(bitmap);
    }

    public byte[][] BuildVuStyles(AtlasLiveSettings settings)
    {
        var shapeCode = VuShapeCodes.TryGetValue(settings.Shape, out var shape) ? shape : VuShapeCodes["bars"];
        var modeCode = VuModeCodes.TryGetValue(settings.MeterMode, out var mode) ? mode : VuModeCodes["stereo"];
        var first = settings.ColorMode.Equals("solid", StringComparison.OrdinalIgnoreCase) ? settings.SolidColor : settings.GradientStart;
        var second = settings.ColorMode.Equals("solid", StringComparison.OrdinalIgnoreCase) ? settings.SolidColor : settings.GradientEnd;

        var styles = new byte[OfficialVuChannelStyles.Length][];
        for (var index = 0; index < OfficialVuChannelStyles.Length; index++)
        {
            var style = OfficialVuChannelStyles[index].ToArray();
            WriteInt32(style, 0x04, shapeCode);
            WriteInt32(style, 0x08, 1);
            WriteInt32(style, 0x0C, modeCode);
            WriteInt32(style, 0x14, 2);
            WriteBgraColor(style, 0x18, first);
            WriteBgraColor(style, 0x1C, second);
            styles[index] = style;
        }

        return styles;
    }

    public static double[] NativeMeterValues(double barLevel, double markerLevel, AtlasLiveSettings settings)
    {
        var bar = Math.Clamp(barLevel, 0, 1);
        var marker = Math.Clamp(markerLevel, 0, 1);
        return [marker, bar, bar, marker, bar, bar];
    }

    private byte[] RenderFallbackIcon(ChannelSlot slot)
    {
        using var bitmap = new Bitmap(IconWidth, IconHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var accent = slot.IsMuted
            ? Color.FromArgb(235, 78, 78)
            : slot.IsActive ? Accents[Math.Clamp(slot.Index, 0, Accents.Length - 1)] : Color.FromArgb(86, 92, 100);
        using var pen = new Pen(accent, 2);
        using var fill = new SolidBrush(Color.FromArgb(22, 25, 31));
        using var textBrush = new SolidBrush(slot.IsMuted ? accent : Color.FromArgb(246, 248, 250));
        using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
        graphics.FillRoundedRectangle(fill, new Rectangle(1, 1, 29, 29), 7);
        graphics.DrawRoundedRectangle(pen, new Rectangle(1, 1, 29, 29), 7);
        var text = slot.IsMuted ? "M" : slot.IsActive ? FirstInitial(slot.ChannelName) : "-";
        DrawCenteredText(graphics, new RectangleF(3, 2, 26, 27), text, font, textBrush);
        return ToBgra(bitmap);
    }

    private byte[]? TryRenderWaveLinkIcon(string imgData)
    {
        try
        {
            var payload = imgData;
            if (payload.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) && payload.Contains(','))
            {
                payload = payload[(payload.IndexOf(',') + 1)..];
            }

            var raw = Convert.FromBase64String(payload);
            using var stream = new MemoryStream(raw);
            using var image = Image.FromStream(stream);
            return RenderImageIcon(image);
        }
        catch
        {
            return null;
        }
    }

    private byte[]? TryRenderImageIcon(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var image = Image.FromFile(path);
            return RenderImageIcon(image);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] RenderImageIcon(Image source)
    {
        using var bitmap = new Bitmap(IconWidth, IconHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        var scale = Math.Min((double)IconWidth / source.Width, (double)IconHeight / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var x = (IconWidth - width) / 2;
        var y = (IconHeight - height) / 2;
        graphics.DrawImage(source, new Rectangle(x, y, width, height));
        return ToBgra(bitmap);
    }

    private static void DrawCenteredText(Graphics graphics, RectangleF rectangle, string text, Font font, Brush brush)
    {
        var size = graphics.MeasureString(text, font, new SizeF(rectangle.Width, rectangle.Height), StringFormat.GenericTypographic);
        var x = rectangle.Left + ((rectangle.Width - size.Width) / 2);
        var y = rectangle.Top + ((rectangle.Height - size.Height) / 2);
        graphics.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
    }

    private static string FitText(Graphics graphics, string text, Font font, int maxWidth)
    {
        if (graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width <= maxWidth)
        {
            return text;
        }

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + ".";
            if (graphics.MeasureString(candidate, font, int.MaxValue, StringFormat.GenericTypographic).Width <= maxWidth)
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string FirstInitial(string name)
    {
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return "?";
    }

    private static void WriteInt32(byte[] bytes, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);

    private static void WriteBgraColor(byte[] bytes, int offset, string value)
    {
        var color = ParseColor(value, Color.White);
        bytes[offset] = color.B;
        bytes[offset + 1] = color.G;
        bytes[offset + 2] = color.R;
        bytes[offset + 3] = 255;
    }

    private static Color ParseColor(string value, Color fallback)
    {
        if (value.Length != 7 || value[0] != '#')
        {
            return fallback;
        }

        try
        {
            return Color.FromArgb(
                Convert.ToInt32(value[1..3], 16),
                Convert.ToInt32(value[3..5], 16),
                Convert.ToInt32(value[5..7], 16));
        }
        catch
        {
            return fallback;
        }
    }

    private static byte[] ToBgra(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowLength = bitmap.Width * 4;
            var pixels = new byte[rowLength * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * rowLength, rowLength);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

internal static class GraphicsRoundedRectangleExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = MakeRoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = MakeRoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath MakeRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
