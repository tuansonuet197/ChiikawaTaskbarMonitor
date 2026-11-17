using System.Runtime.InteropServices;
using UsagiBatteryMonitor.Models;
using UsagiBatteryMonitor.Services;

namespace UsagiBatteryMonitor.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

    private const int WM_DEVICECHANGE = 0x0219;

    private readonly UsagiSpriteCache _sprites = new();
    private readonly BatteryAggregator _aggregator = new();
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiCtx;
    private readonly CancellationTokenSource _appCts = new();

    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _runnerMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _wellnessMenuItem;
    private readonly ToolStripMenuItem _disconnectedBtMenuItem;
    private readonly TrioRunnerCoordinator _trioRunner;
    private readonly BatteryFlyoutForm _flyoutForm;
    private readonly WellnessAdvisor _wellness = new();
    private readonly WellnessBubbleForm _wellnessBubble;
    private readonly DeviceChangeWindow _deviceChangeWnd;
    private readonly System.Windows.Forms.Timer _trayAnimTimer;
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly System.Windows.Forms.Timer _wellnessTimer;

    private IReadOnlyList<BatteryDeviceInfo> _latestDevices = [];
    private DateTime _lastUpdated;
    private readonly HashSet<string> _alertedLow = new(StringComparer.OrdinalIgnoreCase);
    private WellnessPrompt? _pendingPrompt;
    private int _trayTick;
    private bool _isRefreshing;

    public TrayApplicationContext()
    {
        _uiCtx = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        bool firstRun = !SettingsStore.Exists();
        _settings = SettingsStore.Load();
        if (firstRun)
        {
            StartupManager.SetEnabled(true);
            SettingsStore.Save(_settings);
        }

        _menu = new ContextMenuStrip();
        _menu.Items.Add("🔋 Xem pin các thiết bị", null, (_, _) => ShowFlyout(null));
        _menu.Items.Add("🔄 Làm mới ngay", null, (_, _) => _ = TriggerRefreshAsync());
        _menu.Items.Add(new ToolStripSeparator());

        _runnerMenuItem = new ToolStripMenuItem("🐰 Usagi, Chiikawa & Hachiware trên Taskbar")
        {
            Checked = _settings.ShowTaskbarRunner,
            CheckOnClick = false
        };
        _runnerMenuItem.Click += (_, _) => ToggleTaskbarRunner(!_settings.ShowTaskbarRunner);
        _menu.Items.Add(_runnerMenuItem);

        _wellnessMenuItem = new ToolStripMenuItem("💧 Nhắc nghỉ mắt, uống nước & giãn cơ (20/40/60p)")
        {
            Checked = _settings.HealthRemindersEnabled,
            CheckOnClick = false
        };
        _wellnessMenuItem.Click += (_, _) => ToggleWellness(!_settings.HealthRemindersEnabled);
        _menu.Items.Add(_wellnessMenuItem);

        _menu.Items.Add("💬 Nhắc thử một câu ngay", null, (_, _) => ShowWellnessPrompt(_wellness.GetNextPreviewPrompt()));
        _menu.Items.Add("⏱ Đặt lại đồng hồ ngồi máy", null, (_, _) => _wellness.Reset());

        _disconnectedBtMenuItem = new ToolStripMenuItem("📶 Hiện cả thiết bị Bluetooth đã ghép (offline)")
        {
            Checked = _settings.ShowDisconnectedBluetooth,
            CheckOnClick = false
        };
        _disconnectedBtMenuItem.Click += (_, _) =>
        {
            _settings.ShowDisconnectedBluetooth = !_settings.ShowDisconnectedBluetooth;
            _disconnectedBtMenuItem.Checked = _settings.ShowDisconnectedBluetooth;
            SettingsStore.Save(_settings);
            _ = TriggerRefreshAsync();
        };
        _menu.Items.Add(_disconnectedBtMenuItem);

        _startupMenuItem = new ToolStripMenuItem("🚀 Tự khởi động cùng Windows")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = false
        };
        _startupMenuItem.Click += (_, _) => ToggleStartup(!StartupManager.IsEnabled());
        _menu.Items.Add(_startupMenuItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("❌ Thoát", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = _sprites.Usagi.TrayIcons[0],
            Text = "Chiikawa Battery Monitor",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowFlyout(null);
        };

        _flyoutForm = new BatteryFlyoutForm(_sprites, _settings);
        _flyoutForm.RefreshRequested += TriggerRefreshAsync;
        _flyoutForm.TaskbarRunnerToggled += ToggleTaskbarRunner;
        _flyoutForm.StartupToggled += ToggleStartup;
        _flyoutForm.WellnessToggled += ToggleWellness;

        _wellnessBubble = new WellnessBubbleForm(_sprites);

        _trioRunner = new TrioRunnerCoordinator(_sprites, _menu, pt => ShowFlyout(pt));
        _trioRunner.PositionUpdated += () =>
        {
            if (_wellnessBubble.CurrentSpeaker is { } speaker)
                _wellnessBubble.FollowAnchor(_trioRunner.GetSpeakerAnchor(speaker));
        };
        _trioRunner.FullscreenChanged += isFs =>
        {
            if (isFs) _wellnessBubble.HideBubble();
        };
        if (_settings.ShowTaskbarRunner)
            _trioRunner.Start();

        // Cycle Usagi -> Chiikawa -> Hachiware every 24 frames (~3.3s) in the System Tray!
        _trayAnimTimer = new System.Windows.Forms.Timer { Interval = 140 };
        _trayAnimTimer.Tick += (_, _) =>
        {
            _trayTick++;
            int frame = _trayTick % UsagiSpriteCache.FrameCount;
            var charIdx = (PetCharacter)((_trayTick / 24) % 3);
            _trayIcon.Icon = _sprites.Get(charIdx).TrayIcons[frame];
        };
        _trayAnimTimer.Start();

        _debounceTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = TriggerRefreshAsync();
        };
        _deviceChangeWnd = new DeviceChangeWindow(() =>
        {
            _uiCtx.Post(_ =>
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }, null);
        });

        _wellnessTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _wellnessTimer.Tick += (_, _) => OnWellnessTick();
        if (_settings.HealthRemindersEnabled)
            _wellnessTimer.Start();

        _ = RunBackgroundPollLoopAsync(_appCts.Token);
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        TrimMemorySoon();
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
        {
            _trayAnimTimer.Stop();
            _wellnessTimer.Stop();
            _wellnessBubble.HideBubble();
            _wellness.Reset();
            _trioRunner.Stop();
        }
        else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
        {
            _trayAnimTimer.Start();
            _wellness.Reset();
            if (_settings.HealthRemindersEnabled) _wellnessTimer.Start();
            if (_settings.ShowTaskbarRunner) _trioRunner.Start();
            _ = TriggerRefreshAsync();
        }
    }

    private async Task RunBackgroundPollLoopAsync(CancellationToken ct)
    {
        await TriggerRefreshAsync().ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _settings.PollIntervalSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await TriggerRefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TriggerRefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var devices = await _aggregator.RefreshAsync(_settings, _appCts.Token).ConfigureAwait(false);
            _uiCtx.Post(_ => ApplyDeviceResults(devices), null);
        }
        catch { }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplyDeviceResults(IReadOnlyList<BatteryDeviceInfo> devices)
    {
        _latestDevices = devices;
        _lastUpdated = DateTime.UtcNow;

        string tip = BuildTooltipText(devices);
        _trayIcon.Text = tip.Length > 63 ? tip[..62] + "…" : tip;

        if (_flyoutForm.Visible)
        {
            _flyoutForm.UpdateData(_latestDevices, _settings, false, _lastUpdated, _wellness.ContinuousSittingMinutes);
        }

        if (_settings.LowBatteryAlertEnabled)
        {
            foreach (var d in devices)
            {
                if (d.IsConnected && !d.IsCharging && d.BatteryPercent <= _settings.LowBatteryThreshold)
                {
                    if (_alertedLow.Add(d.Id))
                    {
                        _trayIcon.ShowBalloonTip(4000, "Pin yếu ⚡", $"{d.Name} chỉ còn {d.BatteryPercent}% pin!", ToolTipIcon.Warning);
                    }
                }
                else if (d.BatteryPercent > _settings.LowBatteryThreshold + 5 || d.IsCharging)
                {
                    _alertedLow.Remove(d.Id);
                }
            }
        }
    }

    private static string BuildTooltipText(IReadOnlyList<BatteryDeviceInfo> devices)
    {
        if (devices.Count == 0) return "Chiikawa Battery: No devices";
        var parts = devices
            .Where(d => d.Category != DeviceCategory.System)
            .Take(3)
            .Select(d => $"{ShortName(d.Name)} {d.BatteryPercent}%");
        string joined = string.Join(" | ", parts);
        return string.IsNullOrEmpty(joined) ? $"PC {devices[0].BatteryPercent}%" : joined;
    }

    private static string ShortName(string s) => s.Length <= 14 ? s : s[..13] + "…";

    private void ShowFlyout(Point? anchor)
    {
        _flyoutForm.UpdateData(_latestDevices, _settings, _isRefreshing, _lastUpdated, _wellness.ContinuousSittingMinutes);
        _flyoutForm.ShowAtAnchor(anchor);
        _ = TriggerRefreshAsync();
    }

    private void OnWellnessTick()
    {
        var due = _wellness.UpdateAndCheckDue(_settings);
        if (due is not null)
            _pendingPrompt = due;

        if (_pendingPrompt is not null && !_trioRunner.IsFullscreenHidden)
        {
            var toShow = _pendingPrompt;
            _pendingPrompt = null;
            ShowWellnessPrompt(toShow);
        }
    }

    private void ShowWellnessPrompt(WellnessPrompt prompt)
    {
        Point anchor;
        if (_settings.ShowTaskbarRunner && _trioRunner.Visible && !_trioRunner.IsFullscreenHidden)
        {
            _trioRunner.PauseBrieflyForSpeech(42);
            anchor = _trioRunner.GetSpeakerAnchor(prompt.Speaker);
        }
        else
        {
            var work = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            anchor = new Point(work.Right - 160, work.Bottom - 2);
        }

        _wellnessBubble.ShowPrompt(prompt, anchor);
    }

    private void ToggleWellness(bool enable)
    {
        _settings.HealthRemindersEnabled = enable;
        _wellnessMenuItem.Checked = enable;
        SettingsStore.Save(_settings);

        if (enable)
        {
            _wellness.Reset();
            _wellnessTimer.Start();
        }
        else
        {
            _wellnessTimer.Stop();
            _pendingPrompt = null;
            _wellnessBubble.HideBubble();
        }
    }

    private void ToggleTaskbarRunner(bool enable)
    {
        _settings.ShowTaskbarRunner = enable;
        _runnerMenuItem.Checked = enable;
        SettingsStore.Save(_settings);
        if (enable) _trioRunner.Start();
        else _trioRunner.Stop();
    }

    private void ToggleStartup(bool enable)
    {
        StartupManager.SetEnabled(enable);
        _startupMenuItem.Checked = StartupManager.IsEnabled();
    }

    private static void TrimMemorySoon()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            GC.Collect(2, GCCollectionMode.Optimized, false);
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch { }
        });
    }

    private void ExitApp()
    {
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _appCts.Cancel();
        _trayAnimTimer.Stop();
        _debounceTimer.Stop();
        _wellnessTimer.Stop();
        _wellnessBubble.HideBubble();
        _trioRunner.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _flyoutForm.Dispose();
        _wellnessBubble.Dispose();
        _trioRunner.Dispose();
        _deviceChangeWnd.DestroyHandle();
        _sprites.Dispose();
        ExitThread();
    }

    private sealed class DeviceChangeWindow : NativeWindow
    {
        private readonly Action _onChanged;
        public DeviceChangeWindow(Action onChanged)
        {
            _onChanged = onChanged;
            CreateHandle(new CreateParams());
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
                _onChanged();
            base.WndProc(ref m);
        }
    }
}
