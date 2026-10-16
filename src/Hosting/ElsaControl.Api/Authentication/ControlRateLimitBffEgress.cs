using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public sealed class ControlRateLimitBffEgressOptions
{
    public const string ConfigurationKey = "RateLimiting:BffEgressNetworks";

    public string[] Networks { get; set; } = [];
}

internal static class ControlRateLimitBffEgress
{
    internal static string[] ReadNetworks(IConfiguration configuration)
    {
        var section = configuration.GetSection(ControlRateLimitBffEgressOptions.ConfigurationKey);
        var array = section.Get<string[]>();
        if (array is { Length: > 0 })
            return array.SelectMany(Split).ToArray();

        return Split(configuration[ControlRateLimitBffEgressOptions.ConfigurationKey]).ToArray();
    }

    internal static IReadOnlyList<IPNetwork> Parse(IEnumerable<string>? entries)
    {
        var networks = new List<IPNetwork>();
        var errors = Validate(entries, networks).ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Rate limiting BFF egress network configuration is invalid: {string.Join(" ", errors)}");

        return networks;
    }

    internal static IEnumerable<string> Validate(IEnumerable<string>? entries) =>
        Validate(entries, destination: null);

    internal static bool Contains(IPAddress? address, IReadOnlyList<IPNetwork> networks)
    {
        if (address is null || networks.Count == 0)
            return false;

        address = Normalize(address);
        foreach (var network in networks)
        {
            if (network.Contains(address))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> Validate(IEnumerable<string>? entries, List<IPNetwork>? destination)
    {
        if (entries is null)
            yield break;

        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var entry = raw.Trim();
            if (!TryParseNetwork(entry, out var network))
            {
                yield return
                    $"{ControlRateLimitBffEgressOptions.ConfigurationKey} entry '{entry}' is not a valid CIDR or IP address.";
                continue;
            }

            destination?.Add(network);
        }
    }

    private static bool TryParseNetwork(string entry, out IPNetwork network)
    {
        if (IPNetwork.TryParse(entry, out network))
            return true;

        if (!IPAddress.TryParse(entry, out var address))
        {
            network = default;
            return false;
        }

        address = Normalize(address);
        var prefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        network = new IPNetwork(address, prefixLength);
        return true;
    }

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static x => !string.IsNullOrWhiteSpace(x));

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

public sealed class ControlRateLimitBffEgressConfigurationValidator(
    IOptions<ControlRateLimitBffEgressOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = ControlRateLimitBffEgress.Validate(options.Value.Networks).ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Rate limiting BFF egress network configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
