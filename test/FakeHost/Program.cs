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
    ToolDirectory = tunnelDir ?? AppContext.BaseDirectory,
    UseUpnp = false,
    HostName = "FakeHost",
    // H.264 needs Cisco's library, downloaded once next to FakeHost.dll; FAKEHOST_H264=0 turns it off.
    UseH264 = Environment.GetEnvironmentVariable("FAKEHOST_H264") != "0",
}, page);
if (!host.Start()) return 1;
HostServer server = host.Server!;
Console.WriteLine($"CODE {server.JoinCode}");
Console.WriteLine($"PORT {server.Port}");
Console.Out.Flush();

// FAKEHOST_SIZE=2560x1080 mimics an ultrawide host. FAKEHOST_DETAIL=1 adds texture that changes
// completely every frame (the worst case for any encoder); FAKEHOST_DETAIL=2 is more like a game: a
// detailed background that stays put, with 60 things moving over it.
string[] size = (Environment.GetEnvironmentVariable("FAKEHOST_SIZE") ?? "1920x1080").Split('x');
int W = int.Parse(size[0], CultureInfo.InvariantCulture), H = int.Parse(size[1], CultureInfo.InvariantCulture);
bool detail = Environment.GetEnvironmentVariable("FAKEHOST_DETAIL") is "1" or "2";
bool gameLike = Environment.GetEnvironmentVariable("FAKEHOST_DETAIL") == "2";
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
// A pretend P2 loadout with two level-ups waiting, in the plugin's "loadout" message format.
var fakeBalls = new List<(string Name, string Color, int Lvl)> { ("Frost", "#7fd4ff", 1) };
var fakePassives = new List<(string Name, string Color, int Lvl)>();
int fakePicks = Environment.GetEnvironmentVariable("FAKEHOST_PICKS") is string pickCount ? int.Parse(pickCount, CultureInfo.InvariantCulture) : 2;
int fakeOfferId = 1;
var fakeChoices = new[] { ("Bleed", "#e0405a", true, true), ("Frost", "#7fd4ff", true, false), ("Magnet", "#c0c0ff", false, true) };
// After the level-ups, a fuser (FAKEHOST_FUSER=0 leaves it out).
bool fakeFuser = Environment.GetEnvironmentVariable("FAKEHOST_FUSER") != "0";
var fuserChoices = new[]
{
    ("Fission", "fission", "+1-5 upgrade levels at random", "#ffc34d"),
    ("Frost + Bleed", "fusion", "Fusion: the two balls become one", "#7fd4ff"),
    ("Frostburn", "evolution", "Evolution", "#ff7f50"),
};
string? fakeNews = null;
int fakeNewsId = 0;
string FakeLoadout()
{
    string Items(List<(string Name, string Color, int Lvl)> items) => string.Join(",", items.Select(i =>
        FormattableString.Invariant($"{{\"name\":\"{i.Name}\",\"color\":\"{i.Color}\",\"lvl\":{i.Lvl},\"max\":{(i.Lvl >= 3 ? "true" : "false")}}}")));
    string json = FormattableString.Invariant($"{{\"t\":\"loadout\",\"on\":true,\"picks\":{fakePicks},\"balls\":[{Items(fakeBalls)}],\"passives\":[{Items(fakePassives)}]");
    if (fakePicks > 0)
    {
        json += FormattableString.Invariant($",\"offer\":{{\"id\":{fakeOfferId},\"kind\":\"any\",\"wait\":false,\"choices\":[")
            + string.Join(",", fakeChoices.Select(c =>
            {
                int lvl = c.Item4 ? 1 : fakeBalls.Concat(fakePassives).FirstOrDefault(b => b.Name == c.Item1).Lvl + 1;
                return FormattableString.Invariant($"{{\"name\":\"{c.Item1}\",\"ball\":{(c.Item3 ? "true" : "false")},\"new\":{(c.Item4 ? "true" : "false")},\"lvl\":{lvl},\"color\":\"{c.Item2}\"}}");
            })) + "]}";
    }
    else if (fakeFuser)
    {
        json += FormattableString.Invariant($",\"offer\":{{\"id\":{fakeOfferId},\"kind\":\"fuser\",\"wait\":false,\"choices\":[")
            + string.Join(",", fuserChoices.Select(c => FormattableString.Invariant(
                $"{{\"name\":\"{c.Item1}\",\"ball\":true,\"new\":false,\"lvl\":1,\"color\":\"{c.Item4}\",\"opt\":\"{c.Item2}\",\"desc\":\"{c.Item3}\"}}"))) + "]}";
        json = json.Replace("\"picks\":0", "\"picks\":1");
    }
    if (fakeNews != null) json += FormattableString.Invariant($",\"news\":{{\"id\":{fakeNewsId},\"text\":\"{fakeNews}\"}}");
    return json + "}";
}
server.SetLoadout(FakeLoadout());

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
    if (server.TakePick(out int pickedOffer, out int pickedIndex))
    {
        Console.WriteLine($"PICK o={pickedOffer} i={pickedIndex}");
        if (pickedOffer == fakeOfferId && fakePicks > 0 && pickedIndex < fakeChoices.Length)
        {
            var (name, color, ball, isNew) = fakeChoices[pickedIndex];
            var list = ball ? fakeBalls : fakePassives;
            int at = list.FindIndex(i => i.Name == name);
            if (isNew && at < 0) list.Add((name, color, 1));
            else if (at >= 0) list[at] = (name, color, list[at].Lvl + 1);
            // The same three choices again, except what was just taken new comes back as an upgrade.
            fakeChoices[pickedIndex] = (name, color, ball, false);
            fakePicks--;
            fakeOfferId++;
            server.SetLoadout(FakeLoadout());
        }
        else if (pickedOffer == fakeOfferId && fakePicks == 0 && fakeFuser && pickedIndex < fuserChoices.Length)
        {
            // Fission: two random levels (always Frost and the first passive here).
            fakeBalls[0] = (fakeBalls[0].Name, fakeBalls[0].Color, fakeBalls[0].Lvl + 1);
            if (fakePassives.Count > 0) fakePassives[0] = (fakePassives[0].Name, fakePassives[0].Color, fakePassives[0].Lvl + 1);
            fakeNews = $"P2's Fission: +2 upgrade levels ({fakeBalls[0].Name} Lv {fakeBalls[0].Lvl}, {(fakePassives.Count > 0 ? fakePassives[0].Name + " Lv " + fakePassives[0].Lvl : "")}).";
            fakeNewsId++;
            fakeFuser = false;
            fakeOfferId++;
            server.SetLoadout(FakeLoadout());
        }
    }
    // P2 at 75/100; FAKEHOST_DOWN=1 shows P2 knocked out instead.
    bool fakeDown = Environment.GetEnvironmentVariable("FAKEHOST_DOWN") == "1";
    server.SetHostStatus(new HostStatus
    {
        PlayerTwoActive = true, AutoShoot = false, Note = "fake host",
        Health = fakeDown ? 0 : 75, MaxHealth = 100, DownedSeconds = fakeDown ? 12 : 0,
    });

    if (host.Video!.ShouldCapture(now))
    {
        byte[] px = host.Video.GetCaptureBuffer(W, H);
        // Cycle through a few pre-drawn frames (drawing 2560x1080 in C# every frame would make this
        // harness, not the stream, the bottleneck), then stamp P2's box on top.
        byte[] background = backgrounds[gameLike ? 0 : frame % backgrounds.Length];
        Buffer.BlockCopy(background, 0, px, 0, background.Length);
        if (gameLike)
        {
            for (int k = 0; k < 60; k++)
            {
                double t = frame / 30.0 + k * 1.7;
                int sx = (int)((Math.Sin(t * (0.5 + k % 5 * 0.2)) * 0.45 + 0.5) * (W - 40));
                int sy = (int)((Math.Cos(t * (0.3 + k % 7 * 0.15)) * 0.45 + 0.5) * (H - 40));
                FillRect(px, W, H, sx, sy, 24 + k % 3 * 8, (byte)(k * 37), (byte)(255 - k * 11), (byte)(k * 91));
            }
        }
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
        Console.WriteLine(FormattableString.Invariant($"VIDEO fps={host.Video.FramesPerSecond} encode={host.Video.LastEncodeMs:0.0}ms quality={host.Video.CurrentQuality} bytes={host.Video.LastFrameBytes} size={host.Video.OutputWidth}x{host.Video.OutputHeight} codec={host.Video.LastCodec} kbps={host.Video.CurrentBitrate / 1000} h264={server.H264Available}"));
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

static void FillRect(byte[] px, int W, int H, int left, int top, int size, byte r, byte g, byte b)
{
    for (int y = Math.Max(0, top); y < Math.Min(H, top + size); y++)
    {
        int row = (H - 1 - y) * W * 4;
        for (int x = Math.Max(0, left); x < Math.Min(W, left + size); x++)
        {
            int i = row + x * 4;
            px[i] = r; px[i + 1] = g; px[i + 2] = b;
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
