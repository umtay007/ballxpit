using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BALLxPITOnlineCoop.Core;

public sealed class OnlineHostOptions
{
    public int Port = 47777;
    public bool UseCloudflareTunnel = true;
    public bool AllowCloudflaredDownload = true;
    public bool UseUpnp = true;
    public int MaxGuests = 4;
    public int MaxFps = 30;
    public int MaxHeight = 540;
    public int Quality = 60;
    public bool AdaptiveQuality = true;
    /// <summary>Stream H.264 to browsers that can decode it (Cisco's OpenH264, downloaded into <see cref="ToolDirectory"/>).</summary>
    public bool UseH264 = true;
    public bool AllowH264Download = true;
    public int MaxBitrateKbps = 6000;
    public bool StreamAudio = true;
    /// <summary>0 = pick from the CPU count.</summary>
    public int EncoderThreads;
    public string ToolDirectory = ".";
    public string HostName = "Host";
}

public sealed class JoinLink
{
    public string Label = "";
    public string Url = "";
}

/// <summary>Everything a hosting session owns: the server, the streams and the ways in from outside.</summary>
public sealed class OnlineHost : IDisposable
{
    private readonly OnlineHostOptions _options;
    private readonly byte[] _page;
    private CancellationTokenSource? _cts;
    private CloudflareTunnel? _tunnel;
    private UpnpPortMapper? _upnp;
    private string? _lanAddress;

    public OnlineHost(OnlineHostOptions options, byte[] guestPage)
    {
        _options = options;
        _page = guestPage;
    }

    public HostServer? Server { get; private set; }
    public VideoStreamer? Video { get; private set; }
    public AudioStreamer? Audio { get; private set; }
    public bool IsHosting => Server != null;
    public string? LastError { get; private set; }

    public string TunnelStatus => _options.UseCloudflareTunnel ? _tunnel?.Status ?? "" : "Off in the config";
    public string UpnpStatus => _options.UseUpnp ? _upnp?.Status ?? "" : "Off in the config";
    public bool TunnelReady => _tunnel?.IsReady == true && _tunnel.PublicUrl != null;
    public string VideoCodecStatus => !_options.UseH264 ? "H.264 off in the config" : OpenH264Library.Status;
    public bool DirectLinkReady => _upnp?.Mapped == true && _upnp.ExternalAddress != null;

    public bool Start()
    {
        if (IsHosting) return true;
        LastError = null;
        var serverOptions = new ServerOptions
        {
            MaxGuests = Math.Clamp(_options.MaxGuests, 1, 16),
            HostName = _options.HostName,
        };

        HostServer? server = null;
        for (int port = _options.Port; port < _options.Port + 10; port++)
        {
            serverOptions.Port = port;
            var candidate = new HostServer(serverOptions, _page);
            try
            {
                candidate.Start();
                server = candidate;
                break;
            }
            catch (SocketException ex)
            {
                Log.Warn($"Port {port} is not available ({ex.Message}); trying the next one.");
            }
        }
        if (server == null)
        {
            LastError = $"Could not open a port between {_options.Port} and {_options.Port + 9}.";
            Log.Error(LastError);
            return false;
        }

        Server = server;
        int threads = _options.EncoderThreads > 0 ? _options.EncoderThreads : Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        Video = new VideoStreamer(server, threads)
        {
            MaxFps = Math.Clamp(_options.MaxFps, 5, 60),
            MaxHeight = Math.Clamp(_options.MaxHeight, 180, 2160),
            Quality = Math.Clamp(_options.Quality, 10, 95),
            AdaptiveQuality = _options.AdaptiveQuality,
            MaxBitrate = Math.Clamp(_options.MaxBitrateKbps, 300, 50_000) * 1000,
        };
        Video.StartBitrate = Math.Min(Video.MaxBitrate, 2_000_000);
        VideoStreamer video = Video;
        Audio = new AudioStreamer(server) { Enabled = _options.StreamAudio, HalfRate = () => video.LowBandwidth };
        _cts = new CancellationTokenSource();
        _lanAddress = NetworkInfo.LanAddress();

        if (_options.UseH264)
        {
            CancellationToken token = _cts.Token;
            _ = Task.Run(async () =>
            {
                if (await OpenH264Library.EnsureLoadedAsync(_options.ToolDirectory, _options.AllowH264Download, token).ConfigureAwait(false))
                    server.H264Available = true;
            });
        }
        if (_options.UseCloudflareTunnel)
        {
            _tunnel = new CloudflareTunnel(_options.ToolDirectory, _options.AllowCloudflaredDownload);
            _tunnel.Start(server.Port);
        }
        if (_options.UseUpnp)
        {
            _upnp = new UpnpPortMapper();
            UpnpPortMapper upnp = _upnp;
            CancellationToken token = _cts.Token;
            _ = Task.Run(() => upnp.MapAsync(server.Port, token));
        }
        return true;
    }

    public void Stop()
    {
        if (!IsHosting) return;
        try { _cts?.Cancel(); } catch { }
        _tunnel?.Dispose();
        _tunnel = null;
        UpnpPortMapper? upnp = _upnp;
        _upnp = null;
        if (upnp != null) _ = Task.Run(upnp.UnmapAsync);
        Video?.Dispose();
        Audio?.Dispose();
        Server?.Stop();
        Video = null;
        Audio = null;
        Server = null;
    }

    public void Dispose() => Stop();

    /// <summary>Links to give to friends, best first. Each already contains the join code.</summary>
    public List<JoinLink> GetLinks()
    {
        var links = new List<JoinLink>();
        HostServer? server = Server;
        if (server == null) return links;
        string fragment = "/#" + server.JoinCode;
        if (_tunnel?.PublicUrl != null)
            links.Add(new JoinLink { Label = _tunnel.IsReady ? "Internet link" : "Internet link (starting)", Url = _tunnel.PublicUrl + fragment });
        if (_upnp?.Mapped == true && _upnp.ExternalAddress != null)
            links.Add(new JoinLink { Label = "Direct link", Url = $"http://{_upnp.ExternalAddress}:{server.Port}{fragment}" });
        if (_lanAddress != null)
            links.Add(new JoinLink { Label = "Same Wi-Fi link", Url = $"http://{_lanAddress}:{server.Port}{fragment}" });
        return links;
    }
}
