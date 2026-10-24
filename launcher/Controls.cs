using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WorkBuddyRemote;

internal enum ButtonVariant
{
    Primary,
    Outline,
    AccentOutline,
    DangerOutline,
}

internal sealed class RoundedButton : Button
{
    private bool _hover;
    private bool _down;

    public int Radius { get; set; } = 9;
    public ButtonVariant Variant { get; set; } = ButtonVariant.Outline;

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font(Theme.UiFont, 9f);
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _down = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _down = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Gfx.Rounded(rect, (int)Math.Round(Radius * (DeviceDpi / 96f)));

        Color fill;
        Color text;
        Color border = Color.Empty;

        switch (Variant)
        {
            case ButtonVariant.Primary:
                fill = !Enabled ? Color.FromArgb(0xC6, 0xD6, 0xF0)
                    : _down ? Theme.AccentPress
                    : _hover ? Theme.AccentHover
                    : Theme.Accent;
                text = Color.White;
                break;
            case ButtonVariant.AccentOutline:
                fill = !Enabled ? Color.FromArgb(0xF7, 0xF9, 0xFC)
                    : _down ? Color.FromArgb(0xE8, 0xEF, 0xFD)
                    : _hover ? Color.FromArgb(0xF3, 0xF7, 0xFF)
                    : Color.White;
                text = Enabled ? Theme.Accent : Theme.Faint;
                border = Enabled ? Color.FromArgb(0xC9, 0xDA, 0xF8) : Theme.Border;
                break;
            case ButtonVariant.DangerOutline:
                fill = !Enabled ? Color.FromArgb(0xF7, 0xF9, 0xFC)
                    : _down ? Color.FromArgb(0xFB, 0xEA, 0xE8)
                    : _hover ? Color.FromArgb(0xFD, 0xF4, 0xF3)
                    : Color.White;
                text = Enabled ? Theme.Red : Theme.Faint;
                border = Enabled ? Color.FromArgb(0xF0, 0xD3, 0xD0) : Theme.Border;
                break;
            default:
                fill = !Enabled ? Color.FromArgb(0xF7, 0xF9, 0xFC)
                    : _down ? Color.FromArgb(0xEC, 0xF1, 0xFB)
                    : _hover ? Color.FromArgb(0xF6, 0xF9, 0xFF)
                    : Color.White;
                text = Enabled ? Theme.Text : Theme.Faint;
                border = Theme.Border;
                break;
        }

        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }
        if (border != Color.Empty)
        {
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, rect, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

internal sealed class StatusPill : Control
{
    private string _text = "已停止";
    private Color _background = Theme.GraySoft;
    private Color _foreground = Theme.Muted;

    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = new Font(Theme.UiFont, 9f, FontStyle.Bold);
        BackColor = Color.Transparent;
    }

    private int S(int value) => (int)Math.Round(value * (DeviceDpi / 96f));

    public void SetState(string text, Color background, Color foreground)
    {
        _text = text;
        _background = background;
        _foreground = foreground;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Gfx.Rounded(rect, Height / 2);
        using (var brush = new SolidBrush(_background))
        {
            g.FillPath(brush, path);
        }

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int textWidth = TextRenderer.MeasureText(g, _text, Font, Size.Empty, flags).Width;
        int dot = S(7);
        int gap = S(7);
        int group = dot + gap + textWidth;
        int x = Math.Max(S(10), (Width - group) / 2);
        int cy = Height / 2;
        using (var dotBrush = new SolidBrush(_foreground))
        {
            g.FillEllipse(dotBrush, x, cy - dot / 2, dot, dot);
        }
        TextRenderer.DrawText(g, _text, Font, new Point(x + dot + gap, (Height - Font.Height) / 2), _foreground, flags);
    }
}

internal sealed class InfoCard : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    public string LocalUrl { get; set; } = "";
    public string? LanUrl { get; set; }
    public bool BridgeReady { get; set; }
    public bool CdpReady { get; set; }
    public bool WorkBuddyRunning { get; set; }
    public int BridgePort { get; set; }
    public int CdpPort { get; set; }
    public bool IsLan { get; set; }

    public event EventHandler? UrlClicked;

    private bool _hoverUrl;

    public InfoCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Default;
    }

    private int S(int value) => (int)Math.Round(value * (DeviceDpi / 96f));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = e.Y <= S(30);
        if (hover != _hoverUrl)
        {
            _hoverUrl = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverUrl)
        {
            _hoverUrl = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Y <= S(30))
        {
            UrlClicked?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.Rounded(rect, S(12)))
        {
            using var fill = new SolidBrush(Theme.Card);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }

        using var labelFont = new Font(Theme.UiFont, 8.5f);
        using var urlFont = new Font(Theme.UiFont, 9.75f, FontStyle.Bold);
        using var subFont = new Font(Theme.UiFont, 8.5f);
        using var valueFont = new Font(Theme.UiFont, 9f);
        using var suffixFont = new Font(Theme.UiFont, 8.5f);

        // 第一行：访问地址
        TextRenderer.DrawText(g, "访问地址", labelFont, new Point(S(16), S(11)), Theme.Faint, Flags);
        TextRenderer.DrawText(g, LocalUrl, urlFont, new Point(S(82), S(8)),
            _hoverUrl ? Theme.AccentHover : Theme.Accent, Flags);
        var hint = "点击复制";
        int hintWidth = TextRenderer.MeasureText(g, hint, subFont, Size.Empty, Flags).Width;
        TextRenderer.DrawText(g, hint, subFont, new Point(Width - S(16) - hintWidth, S(12)), Theme.Faint, Flags);

        // 第二行：局域网地址 / 未开启提示
        if (!string.IsNullOrWhiteSpace(LanUrl))
        {
            TextRenderer.DrawText(g, "局域网", labelFont, new Point(S(16), S(33)), Theme.Faint, Flags);
            TextRenderer.DrawText(g, LanUrl!, subFont, new Point(S(82), S(32)), Theme.Muted, Flags);
        }
        else
        {
            TextRenderer.DrawText(g, "局域网", labelFont, new Point(S(16), S(33)), Theme.Faint, Flags);
            TextRenderer.DrawText(g, "未开启，仅本机可访问", subFont, new Point(S(82), S(32)), Theme.Faint, Flags);
        }

        int dividerY = S(56);
        using (var pen = new Pen(Theme.Border))
        {
            g.DrawLine(pen, S(16), dividerY, Width - S(16), dividerY);
        }

        DrawStat(g, S(16), S(68), BridgeReady, false, "桥接服务", BridgeReady ? "运行中" : "未运行", $"{BridgePort}", labelFont, valueFont, suffixFont);
        DrawStat(g, S(214), S(68), CdpReady, !CdpReady && WorkBuddyRunning, "桌面端",
            CdpReady ? "运行中" : WorkBuddyRunning ? "需重启" : "未启动", $"{CdpPort}", labelFont, valueFont, suffixFont);
        DrawStat(g, S(404), S(68), true, false, "监听", IsLan ? "局域网" : "仅本机", "", labelFont, valueFont, suffixFont);
    }

    private void DrawStat(Graphics g, int x, int y, bool ok, bool warn, string label, string value, string suffix,
        Font labelFont, Font valueFont, Font suffixFont)
    {
        int dot = S(7);
        int labelOffset = S(14);
        var dotColor = ok ? Theme.Green : warn ? Theme.Amber : Color.FromArgb(0xB8, 0xC0, 0xCB);
        using (var dotBrush = new SolidBrush(dotColor))
        {
            g.FillEllipse(dotBrush, x, y + S(6), dot, dot);
        }

        TextRenderer.DrawText(g, label, labelFont, new Point(x + labelOffset, y), Theme.Faint, Flags);
        int labelWidth = TextRenderer.MeasureText(g, label, labelFont, Size.Empty, Flags).Width;
        int valueX = x + labelOffset + labelWidth + S(10);
        var valueColor = ok ? Theme.Text : warn ? Theme.Amber : Theme.Muted;
        TextRenderer.DrawText(g, value, valueFont, new Point(valueX, y), valueColor, Flags);

        if (!string.IsNullOrEmpty(suffix))
        {
            int valueWidth = TextRenderer.MeasureText(g, value, valueFont, Size.Empty, Flags).Width;
            TextRenderer.DrawText(g, suffix, suffixFont, new Point(valueX + valueWidth + S(6), y + S(1)), Theme.Faint, Flags);
        }
    }
}

internal sealed class CardBox : Panel
{
    public CardBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    private int S(int value) => (int)Math.Round(value * (DeviceDpi / 96f));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Gfx.Rounded(rect, S(10));
        using var fill = new SolidBrush(Theme.Card);
        g.FillPath(fill, path);
        using var pen = new Pen(Theme.Border);
        g.DrawPath(pen, path);
    }
}
