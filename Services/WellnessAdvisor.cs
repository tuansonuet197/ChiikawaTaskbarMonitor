using System.Runtime.InteropServices;
using UsagiBatteryMonitor.UI;

namespace UsagiBatteryMonitor.Services;

public enum WellnessKind
{
    EyeRest,
    HydrationPosture,
    StandStretch,
    LateNight
}

public sealed record WellnessPrompt(
    PetCharacter Speaker,
    WellnessKind Kind,
    string BadgeTitle,
    string Message,
    Color AccentColor);

public sealed class WellnessAdvisor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private const int IdleResetSeconds = 180; // >= 3 minutes away resets continuous sitting timer

    private static readonly Color ChiikawaPink = Color.FromArgb(245, 163, 184);
    private static readonly Color HachiwareBlue = Color.FromArgb(127, 184, 220);
    private static readonly Color UsagiGold = Color.FromArgb(243, 198, 95);
    private static readonly Color NightLavender = Color.FromArgb(182, 164, 245);

    private static readonly string[] EyeMessages =
    [
        "Dán mắt vào màn hình hơi lâu rồi á! Nhìn ra xa 20 giây cho mắt thở xíu nha.",
        "Chớp mắt cái coi! Nghỉ mắt 20 giây nhìn chỗ nào xa xa cho đỡ mỏi nhé.",
        "Mắt cũng biết mệt đó nha, ngẩng đầu nhìn ra ngoài 20 giây rồi làm tiếp nà!"
    ];

    private static readonly string[] HydrationMessages =
    [
        "Alo alo tới giờ uống nước rồi! Nhấp ngụm nước mát và thả hai vai xuống nhé.",
        "Đừng ngồi cong như con tôm nữa! Uống miếng nước rồi ngồi thẳng lưng lên nào.",
        "Nạp nước thôi nè! Xoay nhẹ cổ, thả lỏng vai gáy cho đỡ ê ẩm nha."
    ];

    private static readonly string[] StretchMessages =
    [
        "Ngồi {0} phút rồi đó! Đứng dậy đi lại, vươn vai một cái cho sảng khoái nha.",
        "Yaha! Ngồi {0}p ê người chưa? Đứng lên vươn vai đi vài bước lấy tinh thần nào!",
        "Tới giờ giải lao {0}p! Đứng dậy giãn cơ xíu đi rồi quay lại chiến tiếp nha."
    ];

    private static readonly string[] NightMessages =
    [
        "Khuya rồi á ({0}p ngồi máy)! Nhớ uống nước rồi sớm đi ngủ kẻo mai gấu trúc đó.",
        "Muộn lắm rồi nè! Giãn cơ nhẹ 1 phút xong thu xếp đi ngủ giữ sức khỏe nha."
    ];

    private int _cycleSeconds;
    private int _continuousSittingSeconds;
    private bool _eyeSentInCycle;
    private bool _hydrationSentInCycle;
    private int _variantIdx;
    private int _previewIdx;
    private DateTime _lastTickUtc = DateTime.UtcNow;

    public int ContinuousSittingMinutes => _continuousSittingSeconds / 60;

    public void Reset()
    {
        _cycleSeconds = 0;
        _continuousSittingSeconds = 0;
        _eyeSentInCycle = false;
        _hydrationSentInCycle = false;
        _lastTickUtc = DateTime.UtcNow;
    }

    public WellnessPrompt? UpdateAndCheckDue(AppSettings settings)
    {
        DateTime now = DateTime.UtcNow;
        int elapsed = (int)Math.Clamp((now - _lastTickUtc).TotalSeconds, 0, 30);
        _lastTickUtc = now;

        if (!settings.HealthRemindersEnabled)
            return null;

        if (GetUserIdleSeconds() >= IdleResetSeconds)
        {
            Reset();
            return null;
        }

        _cycleSeconds += elapsed;
        _continuousSittingSeconds += elapsed;

        int eyeSec = Math.Max(5, settings.EyeBreakMinutes) * 60;
        int hydSec = Math.Max(eyeSec + 60, settings.HydrationBreakMinutes * 60);
        int stretchSec = Math.Max(hydSec + 60, settings.StretchBreakMinutes * 60);

        if (_cycleSeconds >= stretchSec)
        {
            _cycleSeconds = 0;
            _eyeSentInCycle = false;
            _hydrationSentInCycle = false;

            int hour = DateTime.Now.Hour;
            int totalMin = Math.Max(1, _continuousSittingSeconds / 60);
            if (hour >= 23 || hour < 5)
            {
                return BuildPrompt(WellnessKind.LateNight, totalMin);
            }
            return BuildPrompt(WellnessKind.StandStretch, totalMin);
        }

        if (!_hydrationSentInCycle && _cycleSeconds >= hydSec)
        {
            _hydrationSentInCycle = true;
            return BuildPrompt(WellnessKind.HydrationPosture, ContinuousSittingMinutes);
        }

        if (!_eyeSentInCycle && _cycleSeconds >= eyeSec)
        {
            _eyeSentInCycle = true;
            return BuildPrompt(WellnessKind.EyeRest, ContinuousSittingMinutes);
        }

        return null;
    }

    public WellnessPrompt GetNextPreviewPrompt()
    {
        var kinds = new[] { WellnessKind.EyeRest, WellnessKind.HydrationPosture, WellnessKind.StandStretch };
        var kind = kinds[_previewIdx % kinds.Length];
        _previewIdx++;
        int mins = Math.Max(20, ContinuousSittingMinutes);
        return BuildPrompt(kind, mins);
    }

    private WellnessPrompt BuildPrompt(WellnessKind kind, int totalMin)
    {
        int idx = _variantIdx++;
        return kind switch
        {
            WellnessKind.EyeRest => new WellnessPrompt(
                PetCharacter.Chiikawa,
                WellnessKind.EyeRest,
                "Chiikawa • Nghỉ mắt xíu nè",
                EyeMessages[idx % EyeMessages.Length],
                ChiikawaPink),

            WellnessKind.HydrationPosture => new WellnessPrompt(
                PetCharacter.Hachiware,
                WellnessKind.HydrationPosture,
                "Hachiware • Uống nước thôi",
                HydrationMessages[idx % HydrationMessages.Length],
                HachiwareBlue),

            WellnessKind.StandStretch => new WellnessPrompt(
                PetCharacter.Usagi,
                WellnessKind.StandStretch,
                "Usagi • Đứng dậy giãn cơ!",
                string.Format(StretchMessages[idx % StretchMessages.Length], totalMin),
                UsagiGold),

            _ => new WellnessPrompt(
                PetCharacter.Usagi,
                WellnessKind.LateNight,
                "Usagi • Ngủ sớm thôi nào",
                string.Format(NightMessages[idx % NightMessages.Length], totalMin),
                NightLavender)
        };
    }

    private static double GetUserIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        uint idleMs = unchecked((uint)Environment.TickCount - lii.dwTime);
        return idleMs / 1000.0;
    }
}
