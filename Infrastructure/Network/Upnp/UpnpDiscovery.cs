#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Network.Upnp;

/// <summary>
/// Minimal SSDP discovery for a UPnP Internet Gateway Device (router).
/// Finds the WANIPConnection/WANPPPConnection control endpoint that
/// UpnpSoapClient uses for port-mapping SOAP calls. No external NuGet
/// dependency — the protocol is three steps:
/// M-SEARCH multicast → LOCATION header → device description XML.
/// </summary>
internal static class UpnpDiscovery
{
    private const string SsdpAddress = "239.255.255.250";
    private const int SsdpPort = 1900;

    /// <summary>Search the LAN for an IGD and return its WAN*Connection control endpoint.</summary>
    internal static async Task<UpnpGatewayEndpoint?> DiscoverAsync(int timeoutMs = 3000)
    {
        List<Uri> locations;
        try
        {
            locations = await SearchLocationsAsync(timeoutMs);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[UPnP] SSDP search failed: {ex.Message}");
            return null;
        }

        foreach (var location in locations)
        {
            try
            {
                var endpoint = await ResolveControlEndpointAsync(location);
                if (endpoint != null) return endpoint;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[UPnP] Skipping device at {location}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// M-SEARCH on every live IPv4 interface in parallel (a machine with virtual
    /// adapters may otherwise multicast out the wrong NIC and miss the router).
    /// </summary>
    private static async Task<List<Uri>> SearchLocationsAsync(int timeoutMs)
    {
        var egressAddresses = UpnpNetworkTopology.GetLocalIPv4Addresses();
        if (egressAddresses.Count == 0)
            egressAddresses.Add(IPAddress.Any);

        var searches = new List<Task<List<Uri>>>();
        foreach (var localAddress in egressAddresses)
            searches.Add(SearchOnInterfaceAsync(localAddress, timeoutMs));

        var found = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var perInterface in await Task.WhenAll(searches))
        {
            foreach (var uri in perInterface)
            {
                if (seen.Add(uri.AbsoluteUri))
                    found.Add(uri);
            }
        }
        return found;
    }

    private static async Task<List<Uri>> SearchOnInterfaceAsync(IPAddress localAddress, int timeoutMs)
    {
        var found = new List<Uri>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(localAddress, 0));
            if (!localAddress.Equals(IPAddress.Any))
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        localAddress.GetAddressBytes());
                }
                catch { /* best effort — routing table decides otherwise */ }
            }

            var target = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
            foreach (var searchTarget in new[]
            {
                "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                "urn:schemas-upnp-org:device:InternetGatewayDevice:2"
            })
            {
                var request =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    $"ST: {searchTarget}\r\n\r\n";
                // Fully qualified: bare "Encoding" resolves to the sibling namespace
                // RemotePlayServer.Infrastructure.Encoding, shadowing System.Text.Encoding.
                var bytes = System.Text.Encoding.ASCII.GetBytes(request);
                await udp.SendAsync(bytes, bytes.Length, target);
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(cts.Token);
                    var text = System.Text.Encoding.ASCII.GetString(result.Buffer);
                    var location = ParseHeader(text, "LOCATION");
                    if (location == null || !Uri.TryCreate(location, UriKind.Absolute, out var uri))
                        continue;

                    // SSRF guard: any LAN device can answer SSDP with an arbitrary URL;
                    // only fetch descriptions hosted on private/link-local addresses.
                    if (!UpnpNetworkTopology.IsTrustedLanUrl(uri))
                    {
                        Logger.Debug($"[UPnP] Ignoring non-LAN device description URL: {uri}");
                        continue;
                    }
                    found.Add(uri);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal: collection window elapsed
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[UPnP] SSDP on {localAddress} failed: {ex.Message}");
        }
        return found;
    }

    private static string? ParseHeader(string response, string name)
    {
        foreach (var line in response.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx > 0 && line.Substring(0, idx).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(idx + 1).Trim();
        }
        return null;
    }

    /// <summary>Fetch the device description XML and locate the WAN*Connection service control URL.</summary>
    private static async Task<UpnpGatewayEndpoint?> ResolveControlEndpointAsync(Uri descriptionUrl)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 256 * 1024 // device descriptions are a few KB
        };
        var xml = await http.GetStringAsync(descriptionUrl);
        var doc = XDocument.Parse(xml);

        // IGDv1 routers may declare an explicit URLBase for relative URLs
        var baseUrl = descriptionUrl;
        foreach (var element in doc.Descendants())
        {
            if (element.Name.LocalName == "URLBase" &&
                Uri.TryCreate(element.Value.Trim(), UriKind.Absolute, out var declaredBase) &&
                IsHttp(declaredBase))
            {
                baseUrl = declaredBase;
                break;
            }
        }

        foreach (var element in doc.Descendants())
        {
            if (element.Name.LocalName != "service") continue;

            var serviceType = ChildValue(element, "serviceType");
            if (serviceType == null) continue;
            if (!serviceType.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) &&
                !serviceType.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase))
                continue;

            var controlPath = ChildValue(element, "controlURL");
            if (string.IsNullOrWhiteSpace(controlPath)) continue;

            // Only trust an absolute URL when it is http(s): on Unix, a path like
            // "/ctl/IPConn" parses as an absolute file:// URI and must be combined instead.
            var controlUrl = Uri.TryCreate(controlPath, UriKind.Absolute, out var absolute) && IsHttp(absolute)
                ? absolute
                : new Uri(baseUrl, controlPath);
            return new UpnpGatewayEndpoint(controlUrl, serviceType);
        }
        return null;
    }

    private static bool IsHttp(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static string? ChildValue(XElement parent, string localName)
    {
        foreach (var child in parent.Elements())
        {
            if (child.Name.LocalName == localName)
                return child.Value.Trim();
        }
        return null;
    }
}

/// <summary>Control endpoint of a discovered UPnP gateway.</summary>
internal sealed record UpnpGatewayEndpoint(Uri ControlUrl, string ServiceType);
