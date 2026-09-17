using System.Net;
using System.Net.Sockets;

namespace ElsaControl.Deployment.Core.Workspace;

/// <summary>
/// Applies one outbound-network policy to every engine health probe. DNS is resolved
/// from the socket connect callback and the socket connects to that checked address,
/// so a second DNS answer cannot change the destination between validation and use.
/// </summary>
public sealed class EngineEndpointHttpClientPolicy(
    Func<string, CancellationToken, Task<IPAddress[]>>? resolveAddresses = null,
    Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveAddresses =
        resolveAddresses ?? ((host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken));

    private readonly Func<IPAddress, int, CancellationToken, ValueTask<Stream>> _connect =
        connect ?? ConnectSocketAsync;

    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxResponseHeadersLength = 16,
        ConnectCallback = (context, cancellationToken) => ConnectEndpointAsync(context.DnsEndPoint, cancellationToken)
    };

    public static bool TryValidateEndpoint(Uri? endpoint, out string message)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            message = "Endpoint address is invalid.";
            return false;
        }

        if (!endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            message = "Endpoint address must use HTTP or HTTPS.";
            return false;
        }

        if (endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
        {
            message = "Endpoint address cannot include user information, query, or fragment data.";
            return false;
        }

        string host;
        try
        {
            host = endpoint.IdnHost.TrimEnd('.');
        }
        catch (UriFormatException)
        {
            message = "Endpoint address is invalid.";
            return false;
        }

        if (IsProhibitedHostName(host)
            || (IPAddress.TryParse(host, out var address) && !IsGloballyRoutable(address)))
        {
            message = "Endpoint address is not publicly routable.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public async ValueTask<Stream> ConnectEndpointAsync(
        DnsEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literalAddress))
            addresses = [literalAddress];
        else
            addresses = await _resolveAddresses(endpoint.Host, cancellationToken);

        if (addresses.Length == 0 || addresses.Any(address => !IsGloballyRoutable(address)))
            throw new EngineEndpointAddressRejectedException();

        Exception? lastConnectFailure = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _connect(address, endpoint.Port, cancellationToken);
            }
            catch (SocketException exception)
            {
                lastConnectFailure = exception;
            }
        }

        throw new HttpRequestException("Could not connect to the verified endpoint address.", lastConnectFailure);
    }

    public static bool IsGloballyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsGloballyRoutable(address.MapToIPv4());

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsGloballyRoutableIpv4(address.GetAddressBytes());

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || !IsInRange(address, "2000::", 3))
            return false;

        // Fail closed for IETF special-use space, including globally reachable exceptions
        // inside 2001::/23, plus transition and documentation ranges. IPv4-mapped addresses
        // are checked as IPv4 above; translation prefixes outside 2000::/3 are denied.
        return !IsInRange(address, "2001::", 23)
            && !IsInRange(address, "2001:db8::", 32)
            && !IsInRange(address, "2002::", 16)
            && !IsInRange(address, "3fff::", 20)
            && !address.IsIPv6LinkLocal
            && !address.IsIPv6SiteLocal
            && !address.IsIPv6Multicast;
    }

    private static bool IsGloballyRoutableIpv4(byte[] address)
    {
        // IANA special-purpose, private, shared, documentation, benchmark, multicast,
        // and reserved ranges. Fail closed for these ranges even if a resolver returns
        // them alongside a public address.
        return !IsInRange(address, "0.0.0.0", 8)
            && !IsInRange(address, "10.0.0.0", 8)
            && !IsInRange(address, "100.64.0.0", 10)
            && !IsInRange(address, "127.0.0.0", 8)
            && !IsInRange(address, "169.254.0.0", 16)
            && !IsInRange(address, "172.16.0.0", 12)
            && !IsInRange(address, "192.0.0.0", 24)
            && !IsInRange(address, "192.0.2.0", 24)
            && !IsInRange(address, "192.88.99.0", 24)
            && !IsInRange(address, "192.168.0.0", 16)
            && !IsInRange(address, "198.18.0.0", 15)
            && !IsInRange(address, "198.51.100.0", 24)
            && !IsInRange(address, "203.0.113.0", 24)
            && !IsInRange(address, "224.0.0.0", 4)
            && !IsInRange(address, "240.0.0.0", 4);
    }

    private static bool IsInRange(IPAddress address, string network, int prefixLength) =>
        IsInRange(address.GetAddressBytes(), IPAddress.Parse(network).GetAddressBytes(), prefixLength);

    private static bool IsInRange(byte[] address, string network, int prefixLength) =>
        IsInRange(address, IPAddress.Parse(network).GetAddressBytes(), prefixLength);

    private static bool IsInRange(byte[] address, byte[] network, int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (address[index] != network[index])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xff << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (network[wholeBytes] & mask);
    }

    private static bool IsProhibitedHostName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("metadata", StringComparison.OrdinalIgnoreCase)
        || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
        || host.Equals("instance-data", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<Stream> ConnectSocketAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal sealed class EngineEndpointAddressRejectedException : HttpRequestException
    {
        public EngineEndpointAddressRejectedException()
            : base("Outbound endpoint address is not permitted.")
        {
        }
    }
}
