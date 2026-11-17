using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UsagiBatteryMonitor.UI;

public enum PetCharacter
{
    Usagi = 0,
    Chiikawa = 1,
    Hachiware = 2
}

public sealed class CharacterSprites : IDisposable
{
    public const int FrameCount = 6;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly IntPtr[] _trayIconHandles = new IntPtr[FrameCount];
    public Icon[] TrayIcons { get; } = new Icon[FrameCount];
    public Bitmap[] RunnerRight { get; } = new Bitmap[FrameCount];
    public Bitmap[] RunnerLeft { get; } = new Bitmap[FrameCount];
    public Bitmap Portrait { get; }

    public CharacterSprites(string prefix)
    {
        var asm = Assembly.GetExecutingAssembly();
        for (int i = 0; i < FrameCount; i++)
        {
            using var s32 = asm.GetManifestResourceStream($"UsagiBatteryMonitor.Assets.{prefix}_32_{i}.png")
                ?? throw new InvalidOperationException($"Missing embedded sprite {prefix}_32_{i}.png");
            using var bmp32 = new Bitmap(s32);
            IntPtr hIcon = bmp32.GetHicon();
            _trayIconHandles[i] = hIcon;
            TrayIcons[i] = Icon.FromHandle(hIcon);

            using var s64 = asm.GetManifestResourceStream($"UsagiBatteryMonitor.Assets.{prefix}_64_{i}.png")
                ?? throw new InvalidOperationException($"Missing embedded sprite {prefix}_64_{i}.png");
            var right = LoadArgb32(s64);
            var left = (Bitmap)right.Clone();
            left.RotateFlip(RotateFlipType.RotateNoneFlipX);

            RunnerRight[i] = right;
            RunnerLeft[i] = left;
        }

        Portrait = (Bitmap)RunnerRight[0].Clone();
    }

    private static Bitmap LoadArgb32(Stream stream)
    {
        using var src = new Bitmap(stream);
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0, src.Width, src.Height);
        return dst;
    }

    public void Dispose()
    {
        for (int i = 0; i < FrameCount; i++)
        {
            TrayIcons[i]?.Dispose();
            if (_trayIconHandles[i] != IntPtr.Zero)
                DestroyIcon(_trayIconHandles[i]);
            RunnerRight[i]?.Dispose();
            RunnerLeft[i]?.Dispose();
        }
        Portrait.Dispose();
    }
}

public sealed class UsagiSpriteCache : IDisposable
{
    public const int FrameCount = CharacterSprites.FrameCount;
    public const int TrioWidth = 176;
    public const int TrioHeight = 64;

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    public CharacterSprites Usagi { get; }
    public CharacterSprites Chiikawa { get; }
    public CharacterSprites Hachiware { get; }

    public IntPtr[] TrioRightHbm { get; } = new IntPtr[FrameCount];
    public IntPtr[] TrioLeftHbm { get; } = new IntPtr[FrameCount];

    public CharacterSprites Get(PetCharacter character) => character switch
    {
        PetCharacter.Chiikawa => Chiikawa,
        PetCharacter.Hachiware => Hachiware,
        _ => Usagi
    };

    public UsagiSpriteCache()
    {
        Usagi = new CharacterSprites("usagi");
        Chiikawa = new CharacterSprites("chiikawa");
        Hachiware = new CharacterSprites("hachiware");

        // Pre-compose 6 right-marching and 6 left-marching squad frames once at startup
        for (int f = 0; f < FrameCount; f++)
        {
            using (var rightStrip = new Bitmap(TrioWidth, TrioHeight, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(rightStrip))
            {
                g.DrawImageUnscaled(Hachiware.RunnerRight[(f + 2) % FrameCount], 0, 0);
                g.DrawImageUnscaled(Chiikawa.RunnerRight[(f + 4) % FrameCount], 56, 0);
                g.DrawImageUnscaled(Usagi.RunnerRight[f], 112, 0);
                TrioRightHbm[f] = rightStrip.GetHbitmap(Color.FromArgb(0));
            }

            using (var leftStrip = new Bitmap(TrioWidth, TrioHeight, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(leftStrip))
            {
                g.DrawImageUnscaled(Usagi.RunnerLeft[f], 0, 0);
                g.DrawImageUnscaled(Chiikawa.RunnerLeft[(f + 4) % FrameCount], 56, 0);
                g.DrawImageUnscaled(Hachiware.RunnerLeft[(f + 2) % FrameCount], 112, 0);
                TrioLeftHbm[f] = leftStrip.GetHbitmap(Color.FromArgb(0));
            }
        }

        // Release intermediate 64x64 bitmaps immediately now that squad HBITMAPs are cached
        foreach (var ch in new[] { Usagi, Chiikawa, Hachiware })
        {
            for (int f = 0; f < FrameCount; f++)
            {
                ch.RunnerRight[f].Dispose();
                ch.RunnerLeft[f].Dispose();
            }
        }
    }

    public void Dispose()
    {
        for (int f = 0; f < FrameCount; f++)
        {
            if (TrioRightHbm[f] != IntPtr.Zero) DeleteObject(TrioRightHbm[f]);
            if (TrioLeftHbm[f] != IntPtr.Zero) DeleteObject(TrioLeftHbm[f]);
        }
        Usagi.Dispose();
        Chiikawa.Dispose();
        Hachiware.Dispose();
    }
}
