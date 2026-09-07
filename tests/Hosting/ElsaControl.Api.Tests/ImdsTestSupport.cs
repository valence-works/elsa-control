using System.Reflection;
using Azure;
using Azure.Core;
using Microsoft.Identity.Client;

namespace ElsaControl.Api.Tests;

/// <summary>Exact IMDS request shapes the Catalog managed-identity policy reasons about.</summary>
internal static class ImdsProbes
{
    public const string Token = "http://169.254.169.254/metadata/identity/oauth2/token";
    public const string Capability = "http://169.254.169.254/metadata/identity/getplatformmetadata?cred-api-version=2.0";
    public const string Compute = "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01";
    public const string Region = "http://169.254.169.254/metadata/instance/compute/location?api-version=2020-06-01&format=text";
}

/// <summary>Counts retry waits without sleeping so pipeline tests stay fast and deterministic.</summary>
internal sealed class RecordingDelay : DelayStrategy
{
    public int Attempts { get; private set; }

    protected override TimeSpan GetNextDelayCore(Response? response, int retryNumber)
    {
        Attempts++;
        return TimeSpan.Zero;
    }
}

/// <summary>
/// MSAL caches managed-identity source discovery process-wide, so only the first credential in a test
/// process performs the IMDS capability probes. Reset it so each real-credential test observes the full sequence.
/// </summary>
internal static class MsalManagedIdentityDiscovery
{
    public static void Reset()
    {
        var client = typeof(ManagedIdentityApplication).Assembly.GetType("Microsoft.Identity.Client.ManagedIdentity.ManagedIdentityClient")
            ?? throw new InvalidOperationException("MSAL ManagedIdentityClient type moved; update the discovery reset hook.");
        var reset = client.GetMethod("ResetSourceForTest", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MSAL ManagedIdentityClient.ResetSourceForTest moved; update the discovery reset hook.");
        reset.Invoke(null, null);
    }
}
