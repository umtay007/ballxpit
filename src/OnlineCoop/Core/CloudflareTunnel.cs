using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// Publishes the local server through a Cloudflare "quick tunnel": cloudflared connects out to
/// Cloudflare and gets a random https://*.trycloudflare.com address that forwards to us. No account,
/// router setup or port forwarding is needed, and it works behind carrier-grade NAT.
/// </summary>
public sealed class CloudflareTunnel : IDisposable
{
    public const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    private static readonly Regex UrlPattern = new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _toolDirectory;
    private readonly bool _allowDownload;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;

    public CloudflareTunnel(string toolDirectory, bool allowDownload)
    {
        _toolDirectory = toolDirectory;
        _allowDownload = allowDownload;
    }

    public string Status { get; private set; } = "Not started";
    public string? PublicUrl { get; private set; }
    public bool IsReady { get; private set; }
    public bool Failed { get; private set; }

    public string ExecutablePath => Path.Combine(_toolDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cloudflared.exe" : "cloudflared");

    public void Start(int localPort)
    {
        _ = Task.Run(() => RunAsync(localPort, _cts.Token));
    }

    private async Task RunAsync(int localPort, CancellationToken token)
    {
        try
        {
            string exe = ExecutablePath;
            if (!File.Exists(exe))
            {
                if (!_allowDownload || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Fail($"cloudflared was not found at {exe}.");
                    return;
                }
                await DownloadAsync(exe, token).ConfigureAwait(false);
            }

            for (int attempt = 1; attempt <= 3 && !token.IsCancellationRequested; attempt++)
            {
                // QUIC (UDP 7844) is the default; some networks block it, so fall back to HTTP/2 over TCP.
                string protocol = attempt == 1 ? "auto" : "http2";
                bool ended = await RunProcessAsync(exe, localPort, protocol, token).ConfigureAwait(false);
                if (!ended || token.IsCancellationRequested) return;
                Log.Warn($"cloudflared exited (attempt {attempt}).");
                PublicUrl = null;
                IsReady = false;
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token).ConfigureAwait(false);
            }
            if (!token.IsCancellationRequested) Fail("cloudflared keeps exiting. See the BepInEx log for its output.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private async Task DownloadAsync(string exe, CancellationToken token)
    {
        Directory.CreateDirectory(_toolDirectory);
        Status = "Downloading cloudflared (one time, ~40 MB)...";
        Log.Info($"Downloading cloudflared from {DownloadUrl}");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BALLxPITOnlineCoop");
        using HttpResponseMessage response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        string temp = exe + ".download";
        await using (Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                done += read;
                Status = total > 0
                    ? $"Downloading cloudflared... {done * 100 / total.Value}%"
                    : $"Downloading cloudflared... {done / (1024 * 1024)} MB";
            }
        }
        using (var check = File.OpenRead(temp))
        {
            if (check.Length < 1024 * 1024 || check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                throw new InvalidDataException("The cloudflared download is not a Windows program.");
        }
        File.Move(temp, exe, true);
        Log.Info($"cloudflared saved to {exe}");
    }

    /// <returns>True if the process ended by itself.</returns>
    private async Task<bool> RunProcessAsync(string exe, int localPort, string protocol, CancellationToken token)
    {
        Status = "Opening the tunnel...";
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = _toolDirectory,
        };
        foreach (string arg in new[] { "tunnel", "--no-autoupdate", "--protocol", protocol, "--url", $"http://127.0.0.1:{localPort}" })
            info.ArgumentList.Add(arg);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        if (!process.Start()) throw new InvalidOperationException("cloudflared did not start.");
        _process = process;
        ChildProcessGuard.Adopt(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Log.Debug("cloudflared: " + line);
        Match match = UrlPattern.Match(line);
        if (match.Success && PublicUrl == null)
        {
            PublicUrl = match.Value;
            Status = "Tunnel address assigned, connecting...";
            Log.Info($"Cloudflare tunnel address: {PublicUrl}");
            string url = PublicUrl;
            _ = Task.Delay(TimeSpan.FromSeconds(40), _cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !IsReady && PublicUrl == url)
                {
                    Status = "Still connecting to Cloudflare. If it never finishes, a firewall or antivirus is blocking cloudflared.exe (it needs outbound port 7844).";
                    Log.Warn(Status);
                }
            }, TaskScheduler.Default);
        }
        if (PublicUrl != null && !IsReady && line.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            IsReady = true;
            Status = "Tunnel is up";
        }
        if (line.IndexOf("failed to request quick Tunnel", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("429 Too Many Requests", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Status = "Cloudflare refused a quick tunnel right now; retrying...";
            Log.Warn("cloudflared: " + line);
        }
    }

    private void Fail(string message)
    {
        Failed = true;
        Status = "Tunnel failed: " + message;
        Log.Warn(Status);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        Process? process = _process;
        _process = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
            }
            process.Dispose();
        }
        IsReady = false;
        PublicUrl = null;
    }
}

/// <summary>
/// Puts child processes in a Windows job object that dies with the game, so cloudflared never
/// outlives it, even after a crash.
/// </summary>
public static class ChildProcessGuard
{
    private static IntPtr _job;

    public static void Adopt(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            if (_job == IntPtr.Zero)
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size)) return;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                _job = job;
            }
            AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not tie cloudflared to the game's lifetime: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
