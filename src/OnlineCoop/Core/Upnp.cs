using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BALLxPITOnlineCoop.Core;

/// <summary>
/// Asks the home router (UPnP IGD) to forward a TCP port to this PC, and reports the router's public
/// address. Many routers have UPnP on; when it's off or the ISP uses carrier-grade NAT this simply
/// fails and the Cloudflare link is still there.
/// </summary>
public sealed class UpnpPortMapper
{
    private static readonly string[] ServiceTypes =
    {
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Uri? _controlUrl;
    private string? _serviceType;
    private int _mappedPort;

    public string Status { get; private set; } = "Not started";
    public string? ExternalAddress { get; private set; }
    public bool Mapped => _mappedPort != 0;

    public async Task<bool> MapAsync(int port, CancellationToken token)
    {
        try
        {
            Status = "Looking for the router...";
            if (!await DiscoverAsync(token).ConfigureAwait(false))
            {
                Status = "No UPnP router answered (UPnP may be off in the router settings).";
                return false;
            }

            string localIp = NetworkInfo.LocalAddressTowards(_controlUrl!.Host) ?? NetworkInfo.LanAddress() ?? "";
            string description = "BALLxPIT Online Coop";
            string args(int lease) =>
                "<NewRemoteHost></NewRemoteHost>"
                + $"<NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>"
                + $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{SecurityElement.Escape(localIp)}</NewInternalClient>"
                + $"<NewEnabled>1</NewEnabled><NewPortMappingDescription>{description}</NewPortMappingDescription>"
                + $"<NewLeaseDuration>{lease}</NewLeaseDuration>";

            // Some routers only accept permanent mappings, others only leases.
            string? error = await SoapAsync("AddPortMapping", args(0), token).ConfigureAwait(false);
            if (error != null) error = await SoapAsync("AddPortMapping", args(7200), token).ConfigureAwait(false);
            if (error != null)
            {
                Status = $"The router refused to open port {port} ({error}).";
                return false;
            }
            _mappedPort = port;

            string? response = await SoapRawAsync("GetExternalIPAddress", "", token).ConfigureAwait(false);
            if (response != null)
            {
                Match m = Regex.Match(response, "<NewExternalIPAddress>([^<]+)</NewExternalIPAddress>");
                if (m.Success) ExternalAddress = m.Groups[1].Value.Trim();
            }
            if (ExternalAddress != null && NetworkInfo.IsPrivateOrShared(ExternalAddress))
            {
                Status = $"Port {port} is open on the router, but your internet provider shares one address between customers (CGNAT), so direct links can't reach you.";
                ExternalAddress = null;
                return false;
            }
            Status = $"Port {port} is open on the router.";
            Log.Info($"UPnP: forwarded TCP {port} to {localIp}; public address {ExternalAddress ?? "unknown"}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Status = "UPnP failed: " + ex.Message;
            Log.Debug(Status);
            return false;
        }
    }

    public async Task UnmapAsync()
    {
        if (_mappedPort == 0 || _controlUrl == null) return;
        int port = _mappedPort;
        _mappedPort = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SoapAsync("DeletePortMapping", $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>", cts.Token).ConfigureAwait(false);
            Log.Info($"UPnP: closed port {port} on the router.");
        }
        catch
        {
        }
    }

    private async Task<bool> DiscoverAsync(CancellationToken token)
    {
        var locations = new HashSet<string>();
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            foreach (string st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2", "upnp:rootdevice" })
            {
                byte[] search = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" + $"ST: {st}\r\n\r\n");
                await udp.SendAsync(search, search.Length, target).ConfigureAwait(false);
            }
            DateTime until = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < until)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(until - DateTime.UtcNow);
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    break;
                }
                Match m = Regex.Match(Encoding.ASCII.GetString(result.Buffer), @"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (m.Success) locations.Add(m.Groups[1].Value.Trim());
            }
        }

        foreach (string location in locations)
        {
            try
            {
                string xml = await _http.GetStringAsync(location, token).ConfigureAwait(false);
                if (TryFindControlUrl(xml, new Uri(location), out Uri? control, out string? type))
                {
                    _controlUrl = control;
                    _serviceType = type;
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"UPnP: could not read {location}: {ex.Message}");
            }
        }
        return false;
    }

    internal static bool TryFindControlUrl(string descriptionXml, Uri location, out Uri? controlUrl, out string? serviceType)
    {
        controlUrl = null;
        serviceType = null;
        XDocument doc = XDocument.Parse(descriptionXml);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        string? baseText = doc.Root?.Element(ns + "URLBase")?.Value;
        Uri baseUri = Uri.TryCreate(baseText, UriKind.Absolute, out Uri? b) ? b : location;
        foreach (string wanted in ServiceTypes)
        {
            XElement? service = doc.Descendants(ns + "service").FirstOrDefault(s => (string?)s.Element(ns + "serviceType") == wanted);
            string? control = service?.Element(ns + "controlURL")?.Value;
            if (string.IsNullOrWhiteSpace(control)) continue;
            controlUrl = new Uri(baseUri, control.Trim());
            serviceType = wanted;
            return true;
        }
        return false;
    }

    /// <returns>Null on success, otherwise the router's error.</returns>
    private async Task<string?> SoapAsync(string action, string arguments, CancellationToken token)
    {
        try
        {
            string? body = await SoapRawAsync(action, arguments, token).ConfigureAwait(false);
            return body == null ? "no answer" : null;
        }
        catch (SoapFault fault)
        {
            return fault.Message;
        }
    }

    private async Task<string?> SoapRawAsync(string action, string arguments, CancellationToken token)
    {
        if (_controlUrl == null || _serviceType == null) return null;
        string envelope = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
            + $"<u:{action} xmlns:u=\"{_serviceType}\">{arguments}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_serviceType}#{action}\"");
        using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Match m = Regex.Match(text, "<errorDescription>([^<]*)</errorDescription>");
            throw new SoapFault(m.Success ? m.Groups[1].Value : $"HTTP {(int)response.StatusCode}");
        }
        return text;
    }

    private sealed class SoapFault : Exception
    {
        public SoapFault(string message) : base(message) { }
    }
}

public static class NetworkInfo
{
    /// <summary>The LAN address other devices at home would use to reach this PC.</summary>
    public static string? LanAddress()
    {
        try
        {
            return LocalAddressTowards("8.8.8.8")
                ?? NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The local address the OS would use to reach <paramref name="host"/> (no packets are sent).</summary>
    public static string? LocalAddressTowards(string host)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, 9);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static bool IsPrivateOrShared(string address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
            || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] == 0;
    }

    /// <summary>Asks a public service which address we appear from.</summary>
    public static async Task<string?> PublicAddressAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string text = (await http.GetStringAsync("https://api.ipify.org", token).ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out _) ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
