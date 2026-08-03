using System.Drawing.Drawing2D;

namespace HerculesWaveBridge;

internal sealed class DisplayRenderer
{
    public Bitmap Render(IReadOnlyList<ChannelSlot> slots, BridgeStatus status)
    {
        var bitmap = new Bitmap(480, 272);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(12, 16, 20));

        using var titleFont = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var frameFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var mutedBrush = new SolidBrush(Color.FromArgb(240, 78, 78));
        using var testBrush = new SolidBrush(Color.FromArgb(255, 210, 68));
        using var dimBrush = new SolidBrush(Color.FromArgb(150, 158, 166));
        using var cyan = new SolidBrush(Color.FromArgb(55, 195, 230));
        using var green = new SolidBrush(Color.FromArgb(77, 210, 145));
        using var borderPen = new Pen(Color.FromArgb(54, 63, 72), 1);

        var frameLabel = DisplayDiagnostics.GetFrameLabel();
        g.DrawString("Wave Link", titleFont, white, 12, 9);
        var statusText = $"{(status.WaveLinkConnected ? "WL OK" : "WL OFF")} | {(status.DeviceConnected ? "USB OK" : "USB OFF")} | {status.TransportMode}";
        g.DrawString(statusText, smallFont, status.LastError.Length == 0 ? green : mutedBrush, 118, 12);
        g.DrawString($"FRAME: {TrimToFit(g, frameLabel.Full, frameFont, 454)}", frameFont, testBrush, 12, 28);

        var slotWidth = 112;
        for (var i = 0; i < 4; i++)
        {
            var slot = i < slots.Count ? slots[i] : ChannelSlot.Empty(i);
            var x = 12 + (i * (slotWidth + 6));
            var y = 50;
            var rect = new Rectangle(x, y, slotWidth, 174);
            using var bgBrush = new SolidBrush(slot.IsActive ? Color.FromArgb(24, 30, 36) : Color.FromArgb(18, 22, 26));
            g.FillRectangle(bgBrush, rect);
            g.DrawRectangle(borderPen, rect);

            var label = TrimToFit(g, slot.ChannelName, labelFont, slotWidth - 12);
            g.DrawString(label, labelFont, slot.IsActive ? white : dimBrush, x + 6, y + 8);
            g.DrawString($"Knob {i + 1}", smallFont, dimBrush, x + 6, y + 28);

            var barOuter = new Rectangle(x + 20, y + 50, slotWidth - 40, 92);
            g.FillRectangle(new SolidBrush(Color.FromArgb(8, 12, 16)), barOuter);
            g.DrawRectangle(borderPen, barOuter);

            var fillHeight = slot.IsActive ? (int)Math.Round((barOuter.Height - 4) * slot.Volume01) : 0;
            var fillRect = new Rectangle(barOuter.X + 2, barOuter.Bottom - 2 - fillHeight, barOuter.Width - 4, fillHeight);
            g.FillRectangle(slot.IsMuted ? mutedBrush : cyan, fillRect);

            var volumeText = slot.IsActive ? $"{Math.Round(slot.Volume01 * 100):0}%" : "--";
            g.DrawString(volumeText, labelFont, slot.IsMuted ? mutedBrush : white, x + 34, y + 146);
            if (slot.IsMuted)
            {
                g.DrawString("MUTED", smallFont, mutedBrush, x + 37, y + 162);
            }
        }

        using var mediaFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        DrawAction(g, 20, 236, "PREV", mediaFont);
        DrawAction(g, 136, 236, "PLAY", mediaFont);
        DrawAction(g, 252, 236, "NEXT", mediaFont);
        DrawAction(g, 368, 236, "UNBOUND", mediaFont);

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            var error = TrimToFit(g, status.LastError, smallFont, 454);
            g.DrawString(error, smallFont, mutedBrush, 12, 222);
        }

        return bitmap;
    }

    private static void DrawAction(Graphics g, int x, int y, string label, Font font)
    {
        var rect = new Rectangle(x, y, 92, 24);
        using var brush = new SolidBrush(Color.FromArgb(30, 36, 42));
        using var pen = new Pen(Color.FromArgb(70, 78, 88), 1);
        using var textBrush = new SolidBrush(Color.FromArgb(218, 226, 232));
        g.FillRectangle(brush, rect);
        g.DrawRectangle(pen, rect);
        var size = g.MeasureString(label, font);
        g.DrawString(label, font, textBrush, rect.X + ((rect.Width - size.Width) / 2), rect.Y + 6);
    }

    private static string TrimToFit(Graphics g, string text, Font font, int maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth)
        {
            return text;
        }

        for (var i = text.Length - 1; i > 0; i--)
        {
            var candidate = text[..i] + "...";
            if (g.MeasureString(candidate, font).Width <= maxWidth)
            {
                return candidate;
            }
        }

        return "...";
    }
}
