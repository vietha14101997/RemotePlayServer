using System.Net;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Tests;

public class RelayAuthErrorTests
{
    [Fact]
    public void FormatAuthError_UsesServerErrorMessage()
    {
        var result = RelayClient.FormatAuthError(
            HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"authentication unavailable: database is not configured\"}");

        Assert.Equal("authentication unavailable: database is not configured", result);
    }

    [Fact]
    public void FormatAuthError_ExplainsMissingRoute()
    {
        var result = RelayClient.FormatAuthError(
            HttpStatusCode.NotFound,
            "{\"message\":\"Not Found\"}");

        Assert.Contains("Relay URL", result);
    }
}
