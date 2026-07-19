using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheria.Server.Net;

/// <summary>
/// Opens the game's UDP port on the home router automatically via UPnP (IGD), so friends can
/// join over the internet with ZERO router configuration — as long as the router has UPnP on
/// (most home boxes do by default). Also asks the router for the public IP, printed at startup
/// so the owner knows what to put in servers.txt. Everything is best-effort with short
/// timeouts: when UPnP is unavailable the server still runs, it just says so.
/// </summary>
public static class UpnpPortOpener
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>Try to map <paramref name="port"/> (UDP) and report through <paramref name="log"/>.</summary>
    public static async Task TryOpenAsync(int port, Action<string> log)
    {
        try
        {
            (string controlUrl, string serviceType)? gateway = await DiscoverGatewayAsync();
            if (gateway is null)
            {
                log("UPnP : routeur introuvable — si tes amis ne peuvent pas rejoindre, " +
                    $"redirige le port UDP {port} manuellement sur ta box.");
                return;
            }

            string localIp = LocalIpAddress();
            bool mapped = await AddPortMappingAsync(gateway.Value.controlUrl, gateway.Value.serviceType,
                port, localIp);
            string publicIp = await GetExternalIpAsync(gateway.Value.controlUrl, gateway.Value.serviceType);

            if (mapped)
            {
                log($"UPnP : port UDP {port} OUVERT automatiquement vers cette machine ({localIp}).");
                log(string.IsNullOrEmpty(publicIp)
                    ? "UPnP : IP publique inconnue — vois ifconfig.me pour la donner à tes amis."
                    : $"IP PUBLIQUE : {publicIp} — tes amis rejoignent via {publicIp}:{port} " +
                      "(mets-la dans le servers.txt du build).");
            }
            else
            {
                log($"UPnP : le routeur a refusé le mappage — redirige le port UDP {port} " +
                    "manuellement sur ta box.");
            }
        }
        catch (Exception e)
        {
            log($"UPnP : indisponible ({e.Message}) — redirection manuelle du port UDP {port} requise.");
        }
    }

    // ------------------------------------------------------------- Discovery

    private static async Task<(string controlUrl, string serviceType)?> DiscoverGatewayAsync()
    {
        // SSDP: multicast M-SEARCH, collect LOCATION headers for internet gateway devices.
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 2500;
        var ssdp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        const string SearchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
        byte[] request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {SearchTarget}\r\n\r\n");
        await udp.SendAsync(request, request.Length, ssdp);
        await udp.SendAsync(request, request.Length, ssdp);

        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            UdpReceiveResult result;
            try
            {
                Task<UdpReceiveResult> receive = udp.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(deadline - DateTime.UtcNow)) != receive)
                {
                    break; // timed out
                }

                result = receive.Result;
            }
            catch (SocketException)
            {
                break;
            }

            string response = Encoding.ASCII.GetString(result.Buffer);
            Match location = Regex.Match(response, @"LOCATION:\s*(\S+)", RegexOptions.IgnoreCase);
            if (!location.Success)
            {
                continue;
            }

            (string controlUrl, string serviceType)? service =
                await FindWanServiceAsync(location.Groups[1].Value.Trim());
            if (service is not null)
            {
                return service;
            }
        }

        return null;
    }

    /// <summary>Read the device description and locate the WAN(IP|PPP)Connection control URL.</summary>
    private static async Task<(string, string)?> FindWanServiceAsync(string locationUrl)
    {
        string xml;
        try
        {
            xml = await Http.GetStringAsync(locationUrl);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (string serviceType in new[]
                 {
                     "urn:schemas-upnp-org:service:WANIPConnection:1",
                     "urn:schemas-upnp-org:service:WANPPPConnection:1",
                 })
        {
            // The <service> block for this type, then its <controlURL>.
            Match block = Regex.Match(xml,
                "<service>\\s*<serviceType>" + Regex.Escape(serviceType) + "</serviceType>(.*?)</service>",
                RegexOptions.Singleline);
            if (!block.Success)
            {
                continue;
            }

            Match control = Regex.Match(block.Groups[1].Value, "<controlURL>(.*?)</controlURL>");
            if (!control.Success)
            {
                continue;
            }

            string controlUrl = control.Groups[1].Value.Trim();
            if (!controlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var baseUri = new Uri(locationUrl);
                controlUrl = new Uri(baseUri, controlUrl).ToString();
            }

            return (controlUrl, serviceType);
        }

        return null;
    }

    // ------------------------------------------------------------------ SOAP

    private static async Task<bool> AddPortMappingAsync(string controlUrl, string serviceType,
        int port, string localIp)
    {
        string body =
            $"<u:AddPortMapping xmlns:u=\"{serviceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            "<NewProtocol>UDP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{localIp}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            "<NewPortMappingDescription>Aetheria</NewPortMappingDescription>" +
            "<NewLeaseDuration>0</NewLeaseDuration>" +
            "</u:AddPortMapping>";

        string? response = await SoapAsync(controlUrl, serviceType, "AddPortMapping", body);
        return response is not null;
    }

    private static async Task<string> GetExternalIpAsync(string controlUrl, string serviceType)
    {
        string body = $"<u:GetExternalIPAddress xmlns:u=\"{serviceType}\"></u:GetExternalIPAddress>";
        string? response = await SoapAsync(controlUrl, serviceType, "GetExternalIPAddress", body);
        if (response is null)
        {
            return string.Empty;
        }

        Match ip = Regex.Match(response, "<NewExternalIPAddress>(.*?)</NewExternalIPAddress>");
        return ip.Success ? ip.Groups[1].Value.Trim() : string.Empty;
    }

    private static async Task<string?> SoapAsync(string controlUrl, string serviceType,
        string action, string body)
    {
        string envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" + body + "</s:Body></s:Envelope>";

        try
        {
            using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", $"\"{serviceType}#{action}\"");
            HttpResponseMessage response = await Http.PostAsync(controlUrl, content);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>This machine's LAN address (the one the router should forward to).</summary>
    private static string LocalIpAddress()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect("8.8.8.8", 65530); // no packet is actually sent for UDP connect
        return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
    }
}
