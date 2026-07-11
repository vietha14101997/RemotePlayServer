#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RemotePlayServer.Infrastructure.Network.Upnp;

/// <summary>
/// SOAP client for the three IGD actions needed for WebRTC port mapping:
/// GetExternalIPAddress, AddPortMapping, DeletePortMapping.
/// </summary>
internal sealed class UpnpSoapClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly UpnpGatewayEndpoint _gateway;

    internal UpnpSoapClient(UpnpGatewayEndpoint gateway) => _gateway = gateway;

    /// <summary>Host (IP) of the gateway control endpoint — used for same-subnet NIC checks.</summary>
    internal string GatewayHost => _gateway.ControlUrl.Host;

    internal async Task<IPAddress?> GetExternalIpAsync()
    {
        var body = await InvokeAsync("GetExternalIPAddress", "");
        var value = ExtractTag(body, "NewExternalIPAddress");
        return IPAddress.TryParse(value, out var ip) ? ip : null;
    }

    /// <summary>Map router UDP port externalPort → internalClient:internalPort. leaseSeconds 0 = permanent.</summary>
    internal async Task AddUdpMappingAsync(int externalPort, int internalPort, string internalClient,
        string description, int leaseSeconds)
    {
        var args =
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{externalPort}</NewExternalPort>" +
            "<NewProtocol>UDP</NewProtocol>" +
            $"<NewInternalPort>{internalPort}</NewInternalPort>" +
            $"<NewInternalClient>{SecurityElement.Escape(internalClient)}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>{SecurityElement.Escape(description)}</NewPortMappingDescription>" +
            $"<NewLeaseDuration>{leaseSeconds}</NewLeaseDuration>";
        await InvokeAsync("AddPortMapping", args);
    }

    internal async Task DeleteUdpMappingAsync(int externalPort)
    {
        var args =
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{externalPort}</NewExternalPort>" +
            "<NewProtocol>UDP</NewProtocol>";
        await InvokeAsync("DeletePortMapping", args);
    }

    private async Task<string> InvokeAsync(string action, string argsXml)
    {
        var envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{action} xmlns:u=\"{_gateway.ServiceType}\">{argsXml}</u:{action}>" +
            "</s:Body></s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, _gateway.ControlUrl)
        {
            // Fully qualified: bare "Encoding" resolves to the sibling namespace
            // RemotePlayServer.Infrastructure.Encoding, shadowing System.Text.Encoding.
            Content = new StringContent(envelope, System.Text.Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_gateway.ServiceType}#{action}\"");

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            int.TryParse(ExtractTag(body, "errorCode"), out var errorCode);
            var errorDescription = ExtractTag(body, "errorDescription") ?? "unknown";
            throw new UpnpSoapException(action, errorCode, errorDescription, (int)response.StatusCode);
        }
        return body;
    }

    private static string? ExtractTag(string xml, string tag)
    {
        var match = Regex.Match(xml, $"<{tag}[^>]*>([^<]*)</", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}

/// <summary>SOAP fault from the gateway. Known codes: 718 = mapping conflict, 725 = only permanent leases.</summary>
internal sealed class UpnpSoapException : Exception
{
    internal const int ConflictInMappingEntry = 718;
    internal const int OnlyPermanentLeasesSupported = 725;

    internal int ErrorCode { get; }

    internal UpnpSoapException(string action, int errorCode, string description, int httpStatus)
        : base($"{action} failed: UPnP error {errorCode} ({description}), HTTP {httpStatus}")
    {
        ErrorCode = errorCode;
    }
}
