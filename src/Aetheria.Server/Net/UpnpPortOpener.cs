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

    /// <summary>Try to map <paramref name="port"/> and report through <paramref name="log"/>.
    /// Protocol is "UDP" (game traffic) or "TCP" (the patch/launcher HTTP server).</summary>
    public static async Task TryOpenAsync(int port, Action<string> log, string protocol = "UDP")
    {
        try
        {
            (string controlUrl, string serviceType)? gateway = await DiscoverGatewayAsync();
            if (gateway is null)
            {
                log($"UPnP : routeur introuvable — si tes amis ne peuvent pas rejoindre, " +
                    $"redirige le port {protocol} {port} manuellement sur ta box.");
                return;
            }

            string localIp = LocalIpAddress();
            bool mapped = await AddPortMappingAsync(gateway.Value.controlUrl, gateway.Value.serviceType,
                port, localIp, protocol);
            string publicIp = await GetExternalIpAsync(gateway.Value.controlUrl, gateway.Value.serviceType);

            if (mapped)
            {
                log($"UPnP : port {protocol} {port} OUVERT automatiquement vers cette machine ({localIp}).");
                log(string.IsNullOrEmpty(publicIp)
                    ? "UPnP : IP publique inconnue — vois ifconfig.me pour la donner à tes amis."
                    : $"IP PUBLIQUE : {publicIp} — tes amis passent par {publicIp}:{port}.");
            }
            else
            {
                log($"UPnP : le routeur a refusé le mappage — redirige le port {protocol} {port} " +
                    "manuellement sur ta box.");
            }
        }
        catch (Exception e)
        {
            log($"UPnP : indisponible ({e.Message}) — redirection manuelle du port {protocol} {port} requise.");
        }
    }

    // ------------------------------------------------------------- Discovery

    private static async Task<(string controlUrl, string serviceType)?> DiscoverGatewayAsync()
    {
        // SSDP M-SEARCH on EVERY IPv4 interface at once: launched as a child process, the
        // OS-picked default interface is sometimes a virtual adapter (Hyper-V/WSL) — probing
        // them all finds the real router no matter which card carries the LAN.
        var ssdp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        const string SearchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
        byte[] request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {SearchTarget}\r\n\r\n");

        var sockets = new List<UdpClient>();
        try
        {
            // One socket bound per local IPv4 address (plus the OS default as a fallback).
            var locals = new List<IPAddress> { IPAddress.Any };
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic in
                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        continue;
                    }

                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation addr in
                             nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(addr.Address))
                        {
                            locals.Add(addr.Address);
                        }
                    }
                }
            }
            catch (Exception) { /* interface enumeration failed: the Any socket remains */ }

            foreach (IPAddress local in locals)
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(local, 0));
                    sockets.Add(udp);
                    await udp.SendAsync(request, request.Length, ssdp);
                    await udp.SendAsync(request, request.Length, ssdp);
                }
                catch (SocketException) { /* that interface can't multicast: skip it */ }
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            var pending = sockets.ToDictionary(s => s.ReceiveAsync(), s => s);
            while (DateTime.UtcNow < deadline && pending.Count > 0)
            {
                Task<UdpReceiveResult> winner;
                {
                    Task first = await Task.WhenAny(
                        pending.Keys.Cast<Task>().Append(Task.Delay(deadline - DateTime.UtcNow)).ToArray());
                    if (first is not Task<UdpReceiveResult> received)
                    {
                        break; // global timeout
                    }

                    winner = received;
                }

                UdpClient socket = pending[winner];
                pending.Remove(winner);
                UdpReceiveResult result;
                try
                {
                    result = winner.Result;
                    pending[socket.ReceiveAsync()] = socket; // keep listening on that socket
                }
                catch (Exception)
                {
                    continue; // socket died: drop it
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
        }
        finally
        {
            foreach (UdpClient socket in sockets)
            {
                try { socket.Dispose(); } catch (Exception) { }
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
        int port, string localIp, string protocol)
    {
        string body =
            $"<u:AddPortMapping xmlns:u=\"{serviceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>{protocol}</NewProtocol>" +
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
