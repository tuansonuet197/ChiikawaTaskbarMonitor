using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using UsagiBatteryMonitor.Models;
using UsagiBatteryMonitor.Services;

namespace UsagiBatteryMonitor.UI;

public sealed class BatteryFlyoutForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private readonly UsagiSpriteCache _sprites;
    private IReadOnlyList<BatteryDeviceInfo> _devices = [];
    private AppSettings _settings;
    private bool _startupEnabled;
    private bool _isRefreshing;
    private DateTime _lastUpdated;
    private int _sittingMinutes;

    private Rectangle _refreshBtnRect;
    private Rectangle _runnerToggleRect;
    private Rectangle _startupToggleRect;
    private Rectangle _wellnessToggleRect;
    private Rectangle _closeBtnRect;

    public event Func<Task>? RefreshRequested;
    public event Action<bool>? TaskbarRunnerToggled;
    public event Action<bool>? StartupToggled;
    public event Action<bool>? WellnessToggled;

    public BatteryFlyoutForm(UsagiSpriteCache sprites, AppSettings settings)
    {
        _sprites = sprites;
        _settings = settings;
        _startupEnabled = StartupManager.IsEnabled();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(26, 28, 36);
        DoubleBuffered = true;
        Width = 356;
        Height = 260;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    public void UpdateData(IReadOnlyList<BatteryDeviceInfo> devices, AppSettings settings, bool isRefreshing, DateTime lastUpdated, int sittingMinutes = 0)
    {
        _devices = devices;
        _settings = settings;
        _startupEnabled = StartupManager.IsEnabled();
        _isRefreshing = isRefreshing;
        _lastUpdated = lastUpdated;
        _sittingMinutes = sittingMinutes;

        int rows = Math.Max(1, devices.Count);
        Height = 66 + rows * 62 + 58;
        Invalidate();
    }

    public void ShowAtAnchor(Point? anchor)
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var work = screen.WorkingArea;
        int x = anchor.HasValue ? anchor.Value.X - Width / 2 : work.Right - Width - 12;
        int y = anchor.HasValue ? anchor.Value.Y - Height - 8 : work.Bottom - Height - 12;

        x = Math.Clamp(x, work.Left + 8, work.Right - Width - 8);
        y = Math.Clamp(y, work.Top + 8, work.Bottom - Height - 8);

        Location = new Point(x, y);
        if (!Visible) Show();
        Activate();
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        if (_refreshBtnRect.Contains(e.Location))
        {
            _isRefreshing = true;
            Invalidate();
            _ = RefreshRequested?.Invoke();
        }
        else if (_runnerToggleRect.Contains(e.Location))
        {
            bool next = !_settings.ShowTaskbarRunner;
            TaskbarRunnerToggled?.Invoke(next);
            Invalidate();
        }
        else if (_startupToggleRect.Contains(e.Location))
        {
            bool next = !_startupEnabled;
            StartupToggled?.Invoke(next);
            _startupEnabled = StartupManager.IsEnabled();
            Invalidate();
        }
        else if (_wellnessToggleRect.Contains(e.Location))
        {
            bool next = !_settings.HealthRemindersEnabled;
            WellnessToggled?.Invoke(next);
            Invalidate();
        }
        else if (_closeBtnRect.Contains(e.Location))
        {
            Hide();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bg = Color.FromArgb(26, 28, 36);
        var cardBg = Color.FromArgb(37, 40, 52);
        var borderCol = Color.FromArgb(56, 60, 76);
        var textPrimary = Color.FromArgb(245, 246, 250);
        var textMuted = Color.FromArgb(160, 166, 184);

        using var borderPen = new Pen(borderCol, 1f);
        using var primaryBrush = new SolidBrush(textPrimary);
        using var mutedBrush = new SolidBrush(textMuted);
        g.Clear(bg);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        // Header with cute Chiikawa trio avatars (Chiikawa in the center!)
        g.DrawImage(_sprites.Hachiware.Portrait, 12, 13, 30, 30);
        g.DrawImage(_sprites.Usagi.Portrait, 54, 10, 34, 34);
        g.DrawImage(_sprites.Chiikawa.Portrait, 32, 13, 32, 32);
        using var titleFont = new Font("Segoe UI Variable Text", 10.5f, FontStyle.Bold);
        using var subFont = new Font("Segoe UI Variable Text", 8.2f, FontStyle.Regular);
        using var rowNameFont = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Bold);
        using var pctFont = new Font("Segoe UI Variable Text", 10.5f, FontStyle.Bold);
        using var pillFont = new Font("Segoe UI Variable Text", 8.0f, FontStyle.Bold);

        g.DrawString("Chiikawa Battery Monitor", titleFont, primaryBrush, 90, 12);
        string timePart = _isRefreshing
            ? "Scanning devices..."
            : (_lastUpdated == default ? "Ready" : $"Updated {_lastUpdated.ToLocalTime():HH:mm:ss}");
        string statusText = _settings.HealthRemindersEnabled && !_isRefreshing
            ? $"{timePart} • Ngồi {_sittingMinutes}p"
            : timePart;
        g.DrawString(statusText, subFont, mutedBrush, 90, 32);

        // Refresh button
        _refreshBtnRect = new Rectangle(Width - 76, 16, 32, 28);
        DrawRoundedButton(g, _refreshBtnRect, cardBg, borderCol, "↻", rowNameFont, textPrimary);

        // Close button
        _closeBtnRect = new Rectangle(Width - 40, 16, 26, 28);
        DrawRoundedButton(g, _closeBtnRect, cardBg, borderCol, "✕", subFont, textMuted);

        // Device cards
        int y = 62;
        if (_devices.Count == 0)
        {
            var emptyRect = new Rectangle(14, y, Width - 28, 52);
            FillRoundedRect(g, emptyRect, 10, cardBg);
            g.DrawString("Chưa thấy thiết bị không dây nào đang kết nối.", subFont, mutedBrush, 26, y + 18);
            y += 62;
        }
        else
        {
            foreach (var dev in _devices)
            {
                var cardRect = new Rectangle(14, y, Width - 28, 54);
                FillRoundedRect(g, cardRect, 10, cardBg);

                // Category badge
                Color col = GetBatteryColor(dev.BatteryPercent, dev.IsCharging);
                DrawCategoryGlyph(g, dev.Category, new Rectangle(24, y + 13, 28, 28), col);

                // Device Name + Subtitle
                string name = dev.Name.Length > 26 ? dev.Name[..25] + "…" : dev.Name;
                g.DrawString(name, rowNameFont, primaryBrush, 60, y + 9);

                string meta = dev.IsCachedSleep
                    ? $"{dev.CategoryLabel} • {dev.Transport} (ngủ)"
                    : dev.IsCharging
                        ? $"{dev.CategoryLabel} • {dev.Transport} ⚡"
                        : dev.VoltageMv.HasValue
                            ? $"{dev.CategoryLabel} • {dev.Transport} • {dev.VoltageMv / 1000.0:0.00}V"
                            : $"{dev.CategoryLabel} • {dev.Transport}";
                g.DrawString(meta, subFont, mutedBrush, 60, y + 29);

                // Percentage text & progress bar
                string pctStr = $"{dev.BatteryPercent}%";
                var pctSize = g.MeasureString(pctStr, pctFont);
                using var pctBrush = new SolidBrush(col);
                g.DrawString(pctStr, pctFont, pctBrush, Width - 24 - pctSize.Width, y + 8);

                var barTrack = new Rectangle(Width - 96, y + 32, 72, 7);
                FillRoundedRect(g, barTrack, 3, Color.FromArgb(55, 59, 75));
                int fillW = Math.Max(4, (int)Math.Round(72.0 * dev.BatteryPercent / 100.0));
                FillRoundedRect(g, new Rectangle(barTrack.X, barTrack.Y, fillW, 7), 3, col);

                y += 62;
            }
        }

        // Bottom quick-toggle bar (3 compact pills)
        int footerY = Height - 44;
        _runnerToggleRect = new Rectangle(14, footerY, 104, 30);
        _startupToggleRect = new Rectangle(126, footerY, 104, 30);
        _wellnessToggleRect = new Rectangle(238, footerY, 104, 30);

        Color activeBg = Color.FromArgb(62, 54, 30);
        Color activeBorder = Color.FromArgb(235, 163, 4);
        Color activeTxt = Color.FromArgb(255, 224, 130);

        Color healthBg = Color.FromArgb(28, 56, 66);
        Color healthBorder = Color.FromArgb(94, 184, 224);
        Color healthTxt = Color.FromArgb(182, 232, 255);

        DrawRoundedButton(g, _runnerToggleRect,
            _settings.ShowTaskbarRunner ? activeBg : cardBg,
            _settings.ShowTaskbarRunner ? activeBorder : borderCol,
            _settings.ShowTaskbarRunner ? "🐰 Trio: ON" : "🐰 Trio: OFF",
            pillFont, _settings.ShowTaskbarRunner ? activeTxt : textMuted);

        DrawRoundedButton(g, _startupToggleRect,
            _startupEnabled ? activeBg : cardBg,
            _startupEnabled ? activeBorder : borderCol,
            _startupEnabled ? "🚀 Start: ON" : "🚀 Start: OFF",
            pillFont, _startupEnabled ? activeTxt : textMuted);

        DrawRoundedButton(g, _wellnessToggleRect,
            _settings.HealthRemindersEnabled ? healthBg : cardBg,
            _settings.HealthRemindersEnabled ? healthBorder : borderCol,
            _settings.HealthRemindersEnabled ? "💧 Nhắc: ON" : "💧 Nhắc: OFF",
            pillFont, _settings.HealthRemindersEnabled ? healthTxt : textMuted);
    }

    private static Color GetBatteryColor(int pct, bool charging)
    {
        if (charging) return Color.FromArgb(92, 214, 141);
        if (pct >= 50) return Color.FromArgb(105, 219, 124);
        if (pct >= 20) return Color.FromArgb(250, 176, 5);
        return Color.FromArgb(255, 107, 107);
    }

    private static void DrawCategoryGlyph(Graphics g, DeviceCategory cat, Rectangle r, Color accent)
    {
        FillRoundedRect(g, r, 7, Color.FromArgb(46, 50, 66));
        string glyph = cat switch
        {
            DeviceCategory.Mouse => "🖱",
            DeviceCategory.Keyboard => "⌨",
            DeviceCategory.Audio => "🎧",
            DeviceCategory.Gamepad => "🎮",
            DeviceCategory.System => "💻",
            _ => "🔋"
        };
        using var f = new Font("Segoe UI Emoji", 11f);
        using var b = new SolidBrush(accent);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, f, b, r, sf);
    }

    private static void DrawRoundedButton(Graphics g, Rectangle rect, Color fill, Color border, string text, Font font, Color fg)
    {
        FillRoundedRect(g, rect, 8, fill);
        using var path = RoundedPath(rect, 8);
        using var pen = new Pen(border, 1f);
        using var b = new SolidBrush(fg);
        g.DrawPath(pen, path);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, b, rect, sf);
    }

    private static void FillRoundedRect(Graphics g, Rectangle rect, int radius, Color color)
    {
        using var path = RoundedPath(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
