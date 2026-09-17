using System.Security.Cryptography;
using ElsaControl.Deployment.Core.ExternalConnections;
using Xunit;

public sealed class RehearsalContractTests
{
    [Fact]
    public void Expiry_rehearsal_refuses_to_send_a_live_pairing()
    {
        var now = DateTimeOffset.UtcNow;
        var bundle = Bundle(now.AddMinutes(5));
        var options = new Options(null, 0, true, "test", "test", "test", "test");

        Assert.Throws<InvalidOperationException>(() => Rehearsal.ValidateMode(options, bundle, now));
    }

    [Fact]
    public void Runtime_routes_preserve_a_control_base_path()
    {
        var bundle = Bundle(DateTimeOffset.UtcNow.AddMinutes(5)) with
        {
            ControlBaseUrl = "https://control.example.test/control"
        };

        bundle.Validate();
        var resolved = new Uri(bundle.ControlEndpoint, Rehearsal.RuntimePath(bundle.ConnectionId, "authenticate"));

        Assert.Equal(
            $"/control/api/runtime/external-engine-connections/{bundle.ConnectionId:D}/authenticate",
            resolved.AbsolutePath);
    }

    [Fact]
    public void Pairing_bundle_string_redacts_the_challenge()
    {
        var bundle = Bundle(DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.DoesNotContain(bundle.Challenge, bundle.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", bundle.ToString(), StringComparison.Ordinal);
    }

    private static PairingBundle Bundle(DateTimeOffset expiresAt)
    {
        var connectionId = Guid.NewGuid();
        return new PairingBundle(
            "elsa-control.external-engine-pairing.v1",
            "https://control.example.test",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            connectionId,
            ExternalEngineEnrollmentDefaults.PairingPurpose,
            ExternalEngineEnrollmentDefaults.AudienceFor(connectionId),
            ExternalEngineEnrollmentProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            DateTimeOffset.UtcNow,
            expiresAt);
    }
}
