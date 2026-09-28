using System.Collections.Concurrent;
using ElsaControl.Api.Workspace;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Test-host pairing gate. Existing protocol tests keep <see cref="AllowAll"/> so they
/// stay focused on enrollment behavior. Gate tests turn that off and either allow a
/// specific organization or rely on <see cref="ExternalEngineOptions"/>.
/// </summary>
internal sealed class TestExternalEnginePairingAvailability(
    IOptions<ExternalEngineOptions> options) : IExternalEnginePairingAvailability
{
    private readonly ConcurrentDictionary<Guid, byte> _allowed = new();

    public bool AllowAll { get; set; } = true;

    public void Allow(Guid organizationId) => _allowed[organizationId] = 0;

    public void ClearAllowed() => _allowed.Clear();

    public bool IsPairingAllowed(Guid organizationId) =>
        AllowAll || _allowed.ContainsKey(organizationId) || options.Value.AllowsPairing(organizationId);
}
