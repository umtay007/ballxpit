using System.Globalization;
using BALLxPITOnlineCoop.Core;

// FakeHost <port> <path to client/index.html> [seconds]
int port = int.Parse(args[0], CultureInfo.InvariantCulture);
byte[] page = File.ReadAllBytes(args[1]);
double runFor = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 3600;
Log.Sink = (level, message) => Console.WriteLine($"LOG {level} {message}");

// Optional 4th argument: a folder containing cloudflared, to test the internet tunnel as well.
string? tunnelDir = args.Length > 3 ? args[3] : null;
var host = new OnlineHost(new OnlineHostOptions
{
    Port = port,
    UseCloudflareTunnel = tunnelDir != null,
    AllowCloudflaredDownload = false,
    ToolDirectory = tunnelDir ?? ".",
    UseUpnp = false,
    HostName = "FakeHost",
}, page);
if (!host.Start()) return 1;
HostServer server = host.Server!;
Console.WriteLine($"CODE {server.JoinCode}");
Console.WriteLine($"PORT {server.Port}");
Console.Out.Flush();

// FAKEHOST_SIZE=2560x1080 mimics an ultrawide host; FAKEHOST_DETAIL=1 adds texture so frames are
// about as big as real game frames.
string[] size = (Environment.GetEnvironmentVariable("FAKEHOST_SIZE") ?? "1920x1080").Split('x');
int W = int.Parse(size[0], CultureInfo.InvariantCulture), H = int.Parse(size[1], CultureInfo.InvariantCulture);
bool detail = Environment.GetEnvironmentVariable("FAKEHOST_DETAIL") == "1";
var audioThread = new Thread(() =>
{
    // 440 Hz left, 660 Hz right, pushed in 10 ms blocks like Unity's mixer would.
    var block = new float[480 * 2];
    long n = 0;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    long pushed = 0;
    while (true)
    {
        long due = (long)(clock.Elapsed.TotalSeconds * 48000);
        while (pushed + 480 <= due)
        {
            for (int i = 0; i < 480; i++, n++)
            {
                block[i * 2] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * n / 48000f);
                block[i * 2 + 1] = 0.3f * MathF.Sin(2 * MathF.PI * 660 * n / 48000f);
            }
            host.Audio!.Push(block, 2, 48000);
            pushed += 480;
        }
        Thread.Sleep(5);
    }
}) { IsBackground = true };
audioThread.Start();

var backgrounds = new byte[4][];
for (int i = 0; i < backgrounds.Length; i++)
{
    backgrounds[i] = new byte[W * H * 4];
    Draw(backgrounds[i], W, H, detail, i * 7, -1000, -1000);
}
var started = DateTime.UtcNow;
int frame = 0;
GuestInput last = default;
bool hadPlayer = false;
double lastStats = 0;
float boxX = W / 2f, boxY = H / 2f;
while ((DateTime.UtcNow - started).TotalSeconds < runFor)
{
    double now = HostServer.Now;
    frame++;
    // Fake "P2": a box that moves with the guest's stick, so the browser test can see input land.
    bool hasPlayer = server.TryGetPlayerInput(out GuestInput input);
    if (hasPlayer && now - input.ReceivedAt < 1)
    {
        boxX = Math.Clamp(boxX + input.MoveX * 8, 0, W - 100);
        boxY = Math.Clamp(boxY - input.MoveY * 8, 0, H - 100);
    }
    if (hasPlayer != hadPlayer || input.MoveX != last.MoveX || input.MoveY != last.MoveY || input.Shoot != last.Shoot || input.AimMode != last.AimMode)
    {
        Console.WriteLine(FormattableString.Invariant($"INPUT player={hasPlayer} mx={input.MoveX:0.00} my={input.MoveY:0.00} am={(int)input.AimMode} ax={input.AimX:0.000} ay={input.AimY:0.000} sh={input.Shoot}"));
        last = input;
        hadPlayer = hasPlayer;
    }
    int toggles = server.TakeAutoShootToggles();
    if (toggles > 0) Console.WriteLine($"TOGGLE {toggles}");
    server.SetHostStatus(new HostStatus { PlayerTwoActive = true, AutoShoot = false, Note = "fake host" });

    if (host.Video!.ShouldCapture(now))
    {
        byte[] px = host.Video.GetCaptureBuffer(W, H);
        // Cycle through a few pre-drawn frames (drawing 2560x1080 in C# every frame would make this
        // harness, not the stream, the bottleneck), then stamp P2's box on top.
        byte[] background = backgrounds[frame % backgrounds.Length];
        Buffer.BlockCopy(background, 0, px, 0, background.Length);
        DrawBox(px, W, H, (int)boxX, (int)boxY);
        host.Video.Submit(W, H, bottomUp: true, now);
    }
    if (now - lastStats > 2)
    {
        lastStats = now;
        foreach (JoinLink link in host.GetLinks()) Console.WriteLine($"LINK {link.Label} {link.Url}");
        if (tunnelDir != null) Console.WriteLine($"TUNNEL {host.TunnelStatus}");
        foreach (GuestInfo g in server.GetGuests())
            Console.WriteLine(FormattableString.Invariant($"GUEST {g.Name} player={g.IsPlayer} ping={g.PingMs} kbps={g.KbitPerSecond:0} window={g.Window} congested={g.Congested} {g.Diagnostics}"));
        Console.WriteLine(FormattableString.Invariant($"VIDEO fps={host.Video.FramesPerSecond} encode={host.Video.LastEncodeMs:0.0}ms quality={host.Video.CurrentQuality} bytes={host.Video.LastFrameBytes} size={host.Video.OutputWidth}x{host.Video.OutputHeight}"));
    }
    Console.Out.Flush();
    Thread.Sleep(16);
}
host.Stop();
return 0;

// Bottom-up RGBA like Texture2D.ReadPixels: row 0 is the bottom of the screen.
static void Draw(byte[] px, int W, int H, bool detail, int frame, int boxX, int boxY)
{
    for (int y = 0; y < H; y++)
    {
        int top = H - 1 - y;
        int row = y * W * 4;
        for (int x = 0; x < W; x++)
        {
            int i = row + x * 4;
            byte r = (byte)(x * 255 / W), g = (byte)(top * 255 / H), b = (byte)((frame * 4) & 255);
            // A red band across the top 10% so the test can check orientation.
            if (top < H / 10) { r = 230; g = 20; b = 20; }
            if (detail)
            {
                // Pixel-art-like texture that changes every frame, like a busy game scene.
                int h = ((x / 3) * 73856093) ^ ((top / 3) * 19349663) ^ (frame * 83492791);
                int n = (h >> 13) & 63;
                r = (byte)Math.Min(255, r / 2 + n);
                g = (byte)Math.Min(255, g / 2 + ((h >> 7) & 63));
                b = (byte)Math.Min(255, b / 2 + ((h >> 19) & 63));
            }
            if (x >= boxX && x < boxX + 100 && top >= boxY && top < boxY + 100) { r = 255; g = 255; b = 255; }
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
        }
    }
}

static void DrawBox(byte[] px, int W, int H, int boxX, int boxY)
{
    for (int top = Math.Max(0, boxY); top < Math.Min(H, boxY + 100); top++)
    {
        int row = (H - 1 - top) * W * 4;
        for (int x = Math.Max(0, boxX); x < Math.Min(W, boxX + 100); x++)
        {
            int i = row + x * 4;
            px[i] = 255; px[i + 1] = 255; px[i + 2] = 255;
        }
    }
}
