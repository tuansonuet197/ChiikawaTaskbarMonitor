using System.Runtime.InteropServices;

namespace UsagiBatteryMonitor.UI;

public sealed class TrioRunnerCoordinator : Form
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
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();

    private readonly UsagiSpriteCache _sprites;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly Action<Point> _onBuddyClicked;

    private int _frame;
    private int _posX;
    private int _posY;
    private int _dir = 1;
    private int _pauseTicks;
    private int _tickCounter;
    private bool _dragging;
    private Point _dragStartMouse;
    private int _dragStartX;
    private bool _movedDuringDrag;
    private bool _hiddenForFullscreen;
    private Rectangle _cachedWorkArea;
    private Rectangle _cachedBounds;

    public TrioRunnerCoordinator(UsagiSpriteCache sprites, ContextMenuStrip menu, Action<Point> onBuddyClicked)
    {
        _sprites = sprites;
        _menu = menu;
        _onBuddyClicked = onBuddyClicked;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Width = UsagiSpriteCache.TrioWidth;
        Height = UsagiSpriteCache.TrioHeight;
        StartPosition = FormStartPosition.Manual;

        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        _cachedWorkArea = screen.WorkingArea;
        _cachedBounds = screen.Bounds;
        _posX = Math.Max(20, _cachedWorkArea.Right - Width - 340);
        _posY = ComputeTaskbarTopY(_cachedWorkArea, _cachedBounds);
        Location = new Point(_posX, _posY);

        _animTimer = new System.Windows.Forms.Timer { Interval = 65 };
        _animTimer.Tick += OnAnimTick;
    }

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

    public bool IsFullscreenHidden => _hiddenForFullscreen;

    public event Action? PositionUpdated;
    public event Action<bool>? FullscreenChanged;

    public Point GetSpeakerAnchor(PetCharacter speaker)
    {
        int offsetX = speaker switch
        {
            PetCharacter.Chiikawa => 88,
            PetCharacter.Hachiware => _dir >= 0 ? 32 : 144,
            _ => _dir >= 0 ? 144 : 32
        };
        return new Point(_posX + offsetX, _posY + 2);
    }

    public void PauseBrieflyForSpeech(int ticks = 40)
    {
        if (Visible && !_hiddenForFullscreen)
            _pauseTicks = Math.Max(_pauseTicks, ticks);
    }

    public void Start()
    {
        if (!Visible) Show();
        RenderFrame();
        _animTimer.Start();
    }

    public void Stop()
    {
        _animTimer.Stop();
        if (Visible) Hide();
    }

    private static int ComputeTaskbarTopY(Rectangle workArea, Rectangle bounds)
    {
        if (workArea.Bottom < bounds.Bottom)
            return workArea.Bottom - 62;
        return bounds.Bottom - 64;
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        _tickCounter++;

        if (_tickCounter % 15 == 0)
        {
            var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            _cachedWorkArea = screen.WorkingArea;
            _cachedBounds = screen.Bounds;

            bool fs = IsForegroundFullscreen();
            if (fs != _hiddenForFullscreen)
            {
                _hiddenForFullscreen = fs;
                Visible = !fs;
                FullscreenChanged?.Invoke(fs);
            }
            if (!fs)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        if (_hiddenForFullscreen || _dragging) return;

        _posY = ComputeTaskbarTopY(_cachedWorkArea, _cachedBounds);

        if (_pauseTicks > 0)
        {
            _pauseTicks--;
            if (_tickCounter % 4 == 0)
            {
                _frame = (_frame == 0) ? 1 : 0;
                RenderFrame();
            }
            return;
        }

        _posX += _dir * 3;
        int minX = _cachedWorkArea.Left + 12;
        int maxX = _cachedWorkArea.Right - Width - 12;
        if (_posX <= minX)
        {
            _posX = minX;
            _dir = 1;
            _pauseTicks = 20;
        }
        else if (_posX >= maxX)
        {
            _posX = maxX;
            _dir = -1;
            _pauseTicks = 20;
        }
        else if (_tickCounter % 120 == 0)
        {
            _pauseTicks = 18;
        }

        _frame = (_frame + 1) % UsagiSpriteCache.FrameCount;
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (!IsHandleCreated || _hiddenForFullscreen) return;

        IntPtr hBmp = _dir >= 0 ? _sprites.TrioRightHbm[_frame] : _sprites.TrioLeftHbm[_frame];
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr oldBmp = IntPtr.Zero;
        try
        {
            oldBmp = SelectObject(memDc, hBmp);
            var size = new SIZE { cx = UsagiSpriteCache.TrioWidth, cy = UsagiSpriteCache.TrioHeight };
            var srcPt = new POINT { x = 0, y = 0 };
            var dstPt = new POINT { x = _posX, y = _posY };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
            PositionUpdated?.Invoke();
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static bool IsForegroundFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow()) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;
        var screen = Screen.FromRectangle(new Rectangle(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top)));
        return r.Left <= screen.Bounds.Left && r.Top <= screen.Bounds.Top
            && r.Right >= screen.Bounds.Right && r.Bottom >= screen.Bounds.Bottom;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _movedDuringDrag = false;
            _dragStartMouse = Cursor.Position;
            _dragStartX = _posX;
        }
        else if (e.Button == MouseButtons.Right)
        {
            _menu.Show(Cursor.Position);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            int dx = Cursor.Position.X - _dragStartMouse.X;
            if (Math.Abs(dx) > 4) _movedDuringDrag = true;
            var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            _posX = Math.Clamp(_dragStartX + dx, screen.WorkingArea.Left + 4, screen.WorkingArea.Right - Width - 4);
            if (dx != 0) _dir = dx > 0 ? 1 : -1;
            RenderFrame();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            bool wasClick = !_movedDuringDrag;
            _dragging = false;
            if (wasClick)
            {
                _onBuddyClicked(new Point(_posX + Width / 2, _posY));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Stop();
            _animTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
