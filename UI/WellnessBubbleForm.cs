using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using UsagiBatteryMonitor.Services;

namespace UsagiBatteryMonitor.UI;

public sealed class WellnessBubbleForm : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x02;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly string PreferredFontFamily = ResolveFontFamily();

    private readonly UsagiSpriteCache _sprites;
    private readonly System.Windows.Forms.Timer _lifeTimer;

    private IntPtr _currentHbm;
    private int _bmpWidth;
    private int _bmpHeight;
    private int _posX;
    private int _posY;
    private int _elapsedTicks;
    private byte _alpha;
    private WellnessPrompt? _activePrompt;

    private const int BubbleWidth = 286;
    private const int TailHeight = 8;
    private const int DisplayTicks = 320; // 320 * 50ms = 16 seconds

    public WellnessBubbleForm(UsagiSpriteCache sprites)
    {
        _sprites = sprites;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Width = BubbleWidth;
        Height = 76;

        _lifeTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _lifeTimer.Tick += OnLifeTick;
    }

    public PetCharacter? CurrentSpeaker => Visible ? _activePrompt?.Speaker : null;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HTCLIENT;
            return;
        }
        base.WndProc(ref m);
    }

    public void ShowPrompt(WellnessPrompt prompt, Point anchorBottomCenter)
    {
        _activePrompt = prompt;
        BuildBubbleBitmap(prompt, anchorBottomCenter);

        _elapsedTicks = 0;
        _alpha = 40;
        if (!Visible)
        {
            SetWindowPos(Handle, HWND_TOPMOST, _posX, _posY, _bmpWidth, _bmpHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Visible = true;
        }
        PushLayeredBitmap(_alpha);
        _lifeTimer.Start();
    }

    public void FollowAnchor(Point anchorBottomCenter)
    {
        if (!Visible || _bmpWidth <= 0) return;
        var (nx, ny) = ComputeBubbleLocation(anchorBottomCenter, _bmpWidth, _bmpHeight);
        if (nx == _posX && ny == _posY) return;
        _posX = nx;
        _posY = ny;
        SetWindowPos(Handle, HWND_TOPMOST, _posX, _posY, _bmpWidth, _bmpHeight, SWP_NOACTIVATE);
    }

    public void HideBubble()
    {
        _lifeTimer.Stop();
        _activePrompt = null;
        if (Visible) Hide();
        ReleaseBitmap();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        HideBubble();
    }

    private void OnLifeTick(object? sender, EventArgs e)
    {
        _elapsedTicks++;

        byte targetAlpha;
        if (_elapsedTicks <= 5)
        {
            targetAlpha = (byte)Math.Min(255, 51 * _elapsedTicks);
        }
        else if (_elapsedTicks >= DisplayTicks - 6)
        {
            int rem = Math.Max(0, DisplayTicks - _elapsedTicks);
            targetAlpha = (byte)(rem * 42);
        }
        else
        {
            targetAlpha = 255;
        }

        if (_elapsedTicks >= DisplayTicks)
        {
            HideBubble();
            return;
        }

        if (targetAlpha != _alpha)
        {
            _alpha = targetAlpha;
            PushLayeredBitmap(_alpha);
        }
    }

    private void BuildBubbleBitmap(WellnessPrompt prompt, Point anchorBottomCenter)
    {
        ReleaseBitmap();

        using var titleFont = new Font(PreferredFontFamily, 8.6f, FontStyle.Bold);
        using var bodyFont = new Font(PreferredFontFamily, 9.0f, FontStyle.Regular);
        using var closeFont = new Font(PreferredFontFamily, 8.0f, FontStyle.Regular);

        int textLeft = 50;
        int textRightPad = 20;
        int textMaxW = BubbleWidth - textLeft - textRightPad;

        int measuredBodyH;
        using (var tmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
        using (var mg = Graphics.FromImage(tmp))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var sz = mg.MeasureString(prompt.Message, bodyFont, textMaxW);
            measuredBodyH = (int)Math.Ceiling(sz.Height);
        }

        int cardH = Math.Clamp(30 + measuredBodyH + 10, 62, 114);
        _bmpWidth = BubbleWidth;
        _bmpHeight = cardH + TailHeight + 2;

        (_posX, _posY) = ComputeBubbleLocation(anchorBottomCenter, _bmpWidth, _bmpHeight);
        int tailCenterX = Math.Clamp(anchorBottomCenter.X - _posX, 24, _bmpWidth - 24);

        using var bmp = new Bitmap(_bmpWidth, _bmpHeight, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var cardRect = new RectangleF(1f, 1f, _bmpWidth - 2f, cardH);
            using var path = BuildSpeechBubblePath(cardRect, 12f, tailCenterX, TailHeight);

            using var bgBrush = new SolidBrush(Color.FromArgb(242, 26, 28, 38));
            using var borderPen = new Pen(Color.FromArgb(190, prompt.AccentColor), 1.3f);
            g.FillPath(bgBrush, path);
            g.DrawPath(borderPen, path);

            // Tiny speaker avatar pill on the left
            var avatarBgRect = new Rectangle(11, (cardH - 30) / 2, 30, 30);
            using var avatarBg = new SolidBrush(Color.FromArgb(46, prompt.AccentColor));
            FillCircle(g, avatarBgRect, avatarBg);
            g.DrawImage(_sprites.Get(prompt.Speaker).Portrait, avatarBgRect.X + 2, avatarBgRect.Y + 2, 26, 26);

            // Title & close hint
            using var titleBrush = new SolidBrush(prompt.AccentColor);
            using var bodyBrush = new SolidBrush(Color.FromArgb(242, 244, 249));
            using var mutedBrush = new SolidBrush(Color.FromArgb(150, 158, 178));

            g.DrawString(prompt.BadgeTitle, titleFont, titleBrush, textLeft, 9f);
            g.DrawString("✕", closeFont, mutedBrush, _bmpWidth - 20f, 8f);

            var bodyRect = new RectangleF(textLeft, 27f, textMaxW + 4f, cardH - 34f);
            g.DrawString(prompt.Message, bodyFont, bodyBrush, bodyRect);
        }

        _currentHbm = bmp.GetHbitmap(Color.FromArgb(0));
    }

    private (int x, int y) ComputeBubbleLocation(Point anchorBottomCenter, int w, int h)
    {
        var screen = Screen.FromPoint(anchorBottomCenter);
        var work = screen.WorkingArea;
        int x = Math.Clamp(anchorBottomCenter.X - w / 2, work.Left + 8, work.Right - w - 8);
        int y = Math.Max(work.Top + 8, anchorBottomCenter.Y - h - 2);
        return (x, y);
    }

    private void PushLayeredBitmap(byte alpha)
    {
        if (!IsHandleCreated || _currentHbm == IntPtr.Zero) return;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr oldBmp = IntPtr.Zero;
        try
        {
            oldBmp = SelectObject(memDc, _currentHbm);
            var size = new SIZE { cx = _bmpWidth, cy = _bmpHeight };
            var srcPt = new POINT { x = 0, y = 0 };
            var dstPt = new POINT { x = _posX, y = _posY };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = AC_SRC_ALPHA
            };
            UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseBitmap()
    {
        if (_currentHbm != IntPtr.Zero)
        {
            DeleteObject(_currentHbm);
            _currentHbm = IntPtr.Zero;
        }
    }

    private static GraphicsPath BuildSpeechBubblePath(RectangleF r, float radius, float tailX, float tailH)
    {
        float d = radius * 2f;
        float tailHalfW = 7f;
        float left = r.X;
        float top = r.Y;
        float right = r.Right;
        float bottom = r.Bottom;

        float txLeft = Math.Clamp(tailX - tailHalfW, left + radius + 4f, right - radius - tailHalfW * 2f - 4f);
        float txMid = txLeft + tailHalfW;
        float txRight = txMid + tailHalfW;

        var path = new GraphicsPath();
        path.AddArc(left, top, d, d, 180, 90);
        path.AddArc(right - d, top, d, d, 270, 90);
        path.AddArc(right - d, bottom - d, d, d, 0, 90);
        path.AddLine(right - radius, bottom, txRight, bottom);
        path.AddLine(txRight, bottom, txMid, bottom + tailH);
        path.AddLine(txMid, bottom + tailH, txLeft, bottom);
        path.AddLine(txLeft, bottom, left + radius, bottom);
        path.AddArc(left, bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillCircle(Graphics g, Rectangle r, Brush brush)
    {
        g.FillEllipse(brush, r);
    }

    private static string ResolveFontFamily()
    {
        try
        {
            using var col = new InstalledFontCollection();
            foreach (var fam in col.Families)
            {
                if (fam.Name.Equals("Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase))
                    return "Segoe UI Variable Text";
            }
        }
        catch { }
        return "Segoe UI";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifeTimer.Stop();
            _lifeTimer.Dispose();
        }
        ReleaseBitmap();
        base.Dispose(disposing);
    }
}
