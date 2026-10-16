using System.Net;
using ElsaControl.Api.Authentication;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ControlRateLimitBffEgressTests
{
    [Fact]
    public async Task Empty_networks_are_accepted()
    {
        await new ControlRateLimitBffEgressConfigurationValidator(
            Options.Create(new ControlRateLimitBffEgressOptions())).StartAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("not-a-network")]
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.0/-1")]
    public async Task Invalid_networks_are_refused(string entry)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ControlRateLimitBffEgressConfigurationValidator(
                Options.Create(new ControlRateLimitBffEgressOptions { Networks = [entry] }))
                .StartAsync(CancellationToken.None));

        Assert.Contains(ControlRateLimitBffEgressOptions.ConfigurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains(entry, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("203.0.113.0/24")]
    [InlineData("2001:db8::/32")]
    [InlineData("::1")]
    public async Task Valid_networks_are_accepted(string entry)
    {
        await new ControlRateLimitBffEgressConfigurationValidator(
            Options.Create(new ControlRateLimitBffEgressOptions { Networks = [entry] }))
            .StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Contains_matches_inside_and_rejects_outside()
    {
        var networks = ControlRateLimitBffEgress.Parse(["203.0.113.10/32"]);

        Assert.True(ControlRateLimitBffEgress.Contains(IPAddress.Parse("203.0.113.10"), networks));
        Assert.False(ControlRateLimitBffEgress.Contains(IPAddress.Parse("203.0.113.11"), networks));
        Assert.False(ControlRateLimitBffEgress.Contains(null, networks));
        Assert.False(ControlRateLimitBffEgress.Contains(IPAddress.Parse("203.0.113.10"), []));
    }

    [Fact]
    public void Contains_matches_ipv4_mapped_address()
    {
        var networks = ControlRateLimitBffEgress.Parse(["203.0.113.10/32"]);

        Assert.True(ControlRateLimitBffEgress.Contains(IPAddress.Parse("::ffff:203.0.113.10"), networks));
    }
}
