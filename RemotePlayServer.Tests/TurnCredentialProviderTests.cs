using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.Tests;

/// <summary>
/// Tests for locally-minted coturn REST credentials (use-auth-secret scheme).
/// The HMAC vectors were computed independently (Python hmac/sha1) and match the
/// relay's Go implementation in RelaySignalingServer/internal/turn/credentials.go:
/// username = "{unixExpiry}:{userId}", credential = Base64(HMAC-SHA1(secret, username)).
/// </summary>
public class TurnCredentialProviderTests
{
    [Theory]
    [InlineData("test-shared-secret", "1752200000:host", "6VjFDqckGW2uvfaHkfOa8yCMgeA=")]
    [InlineData("test-shared-secret", "1752200000:client", "bidRAbAUFZKHmXlGi1PuhG3NVM0=")]
    public void ComputeCredential_MatchesCoturnRestScheme(string secret, string username, string expected)
    {
        Assert.Equal(expected, TurnCredentialProvider.ComputeCredential(secret, username));
    }

    [Fact]
    public void MintIceServer_ProducesUdpAndTcpUrlsWithEphemeralUsername()
    {
        TurnCredentialProvider.Configure(new InternetConfig
        {
            TurnHost = "203.0.113.10",
            TurnPort = 3478,
            TurnSecret = "test-shared-secret",
            TurnTtlHours = 24
        });
        try
        {
            var server = TurnCredentialProvider.MintIceServer("host");

            Assert.NotNull(server);
            Assert.Equal(2, server!.Urls.Count);
            Assert.Contains("turn:203.0.113.10:3478?transport=udp", server.Urls);
            Assert.Contains("turn:203.0.113.10:3478?transport=tcp", server.Urls);

            // Username = "{expiry}:host" with expiry ~24h from now
            var parts = server.Username!.Split(':', 2);
            Assert.Equal("host", parts[1]);
            var expiry = long.Parse(parts[0]);
            var expected = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
            Assert.InRange(expiry, expected - 30, expected + 30);

            // Credential must verify against the same HMAC computation
            Assert.Equal(
                TurnCredentialProvider.ComputeCredential("test-shared-secret", server.Username!),
                server.Credential);
        }
        finally
        {
            TurnCredentialProvider.Configure(null);
        }
    }

    [Fact]
    public void MintIceServer_ReturnsNullWithoutConfig()
    {
        TurnCredentialProvider.Configure(null);
        Assert.Null(TurnCredentialProvider.MintIceServer());

        TurnCredentialProvider.Configure(new InternetConfig { TurnHost = "1.2.3.4" }); // no secret
        try
        {
            Assert.Null(TurnCredentialProvider.MintIceServer());
        }
        finally
        {
            TurnCredentialProvider.Configure(null);
        }
    }

    [Fact]
    public void BuildIceServerList_IncludesStunWithoutTurnOrRelay()
    {
        var servers = TurnCredentialProvider.BuildIceServerList(null, null);

        var stun = Assert.Single(servers);
        Assert.Contains("stun:stun.l.google.com:19302", stun.Urls);
        Assert.Contains("stun:stun1.l.google.com:19302", stun.Urls);
        Assert.Null(stun.Username);
        Assert.Null(stun.Credential);
    }

    [Fact]
    public void BuildIceServerList_DeduplicatesStunButPreservesTurnCredentials()
    {
        var localTurn = new IceServerDto
        {
            Urls = new List<string> { "turn:turn.example.com:3478?transport=udp" },
            Username = "local-user",
            Credential = "local-credential"
        };
        var relayIce = new List<IceServerConfig>
        {
            new()
            {
                Urls = new List<string>
                {
                    "stun:stun.l.google.com:19302",
                    "stun:stun1.l.google.com:19302"
                }
            },
            new()
            {
                Urls = new List<string> { "turn:turn.example.com:3478?transport=udp" },
                Username = "relay-user",
                Credential = "relay-credential"
            },
            new() { Urls = null! },
            new() { Urls = new List<string> { null! } }
        };

        var servers = TurnCredentialProvider.BuildIceServerList(localTurn, relayIce);

        Assert.Equal(3, servers.Count);
        Assert.Single(servers, server => server.Urls.Any(url => url.StartsWith("stun:")));
        Assert.Contains(servers, server =>
            server.Username == "local-user" && server.Credential == "local-credential");
        Assert.Contains(servers, server =>
            server.Username == "relay-user" && server.Credential == "relay-credential");
    }
}
