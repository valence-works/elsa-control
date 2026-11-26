using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class PulumiEngineEventMapperTests
{
    [Fact]
    public void Resource_pre_event_maps_to_a_customer_safe_started_step()
    {
        var operationId = Guid.NewGuid();
        var mapped = PulumiEngineEventMapper.Map(
            operationId,
            new PulumiEngineEventFacts(
                Sequence: 1,
                Cancelled: false,
                PreResourceType: "azure-native:resources:ResourceGroup",
                OutputsResourceType: null,
                FailedResourceType: null,
                DiagnosticError: false,
                Summary: false),
            DateTimeOffset.UtcNow).Single();

        Assert.Equal(ProvisioningEventKind.ResourceStarted, mapped.Kind);
        Assert.Equal("resource-group", mapped.ResourceKind);
        Assert.Equal(PulumiEngineEventMapper.ResourceStartedMessage, mapped.Message);
        Assert.Equal(1, mapped.EngineSequence);
        Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(mapped.Message));
        Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(mapped.ResourceKind));
    }

    [Theory]
    [InlineData("azure-native:managedidentity:UserAssignedIdentity", "identity")]
    [InlineData("azure-native:keyvault:Vault", ProvisioningCapability.Secrets)]
    [InlineData("azure-native:sql:Server", ProvisioningCapability.ManagedSql)]
    [InlineData("azure-native:app:ManagedEnvironment", ProvisioningCapability.SharedEnvironment)]
    [InlineData("azure-native:app:ContainerApp", ProvisioningCapability.ContainerRuntime)]
    [InlineData("azure-native:authorization:RoleAssignment", "role-assignment")]
    [InlineData("azure-native:operationalinsights:Workspace", "observability")]
    public void Resource_types_map_to_capability_kinds(string type, string expected)
    {
        Assert.Equal(expected, PulumiEngineEventMapper.ResourceKind(type));
    }

    [Fact]
    public void Diagnostic_errors_never_copy_raw_provider_text()
    {
        var mapped = PulumiEngineEventMapper.Map(
            Guid.NewGuid(),
            new PulumiEngineEventFacts(
                Sequence: 9,
                Cancelled: false,
                PreResourceType: null,
                OutputsResourceType: null,
                FailedResourceType: null,
                DiagnosticError: true,
                Summary: false),
            DateTimeOffset.UtcNow).Single();

        Assert.Equal(ProvisioningEventKind.ResourceFailed, mapped.Kind);
        Assert.Equal(PulumiEngineEventMapper.ResourceFailedMessage, mapped.Message);
        Assert.DoesNotContain("subscription", mapped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", mapped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resourcegroups", mapped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(mapped.Message));
    }

    [Fact]
    public void Resource_operation_failures_use_the_resource_kind_and_a_safe_message()
    {
        var mapped = PulumiEngineEventMapper.Map(
            Guid.NewGuid(),
            new PulumiEngineEventFacts(
                Sequence: 8,
                Cancelled: false,
                PreResourceType: null,
                OutputsResourceType: null,
                FailedResourceType: "azure-native:sql:Server",
                DiagnosticError: false,
                Summary: false),
            DateTimeOffset.UtcNow).Single();

        Assert.Equal(ProvisioningEventKind.ResourceFailed, mapped.Kind);
        Assert.Equal(ProvisioningCapability.ManagedSql, mapped.ResourceKind);
        Assert.Equal(PulumiEngineEventMapper.ResourceFailedMessage, mapped.Message);
        Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(mapped.Message));
    }

    [Fact]
    public void Resource_identifiers_in_types_are_dropped()
    {
        Assert.Equal(
            ProvisioningEventSanitizer.DefaultResourceKind,
            PulumiEngineEventMapper.ResourceKind(
                "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/engine-rg"));
    }

    [Fact]
    public void Cancel_and_summary_are_ordered_by_engine_sequence()
    {
        var operationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            PulumiEngineEventMapper.Map(operationId, new PulumiEngineEventFacts(3, false, null, null, null, false, true), now),
            PulumiEngineEventMapper.Map(
                operationId,
                new PulumiEngineEventFacts(1, false, "azure-native:resources:ResourceGroup", null, null, false, false),
                now),
            PulumiEngineEventMapper.Map(operationId, new PulumiEngineEventFacts(2, true, null, null, null, false, false), now)
        }.SelectMany(item => item).OrderBy(item => item.EngineSequence).ToArray();

        Assert.Equal(new int?[] { 1, 2, 3 }, events.Select(item => item.EngineSequence).ToArray());
        Assert.Equal(ProvisioningEventKind.ResourceStarted, events[0].Kind);
        Assert.Equal(ProvisioningEventKind.OperationCancelled, events[1].Kind);
        Assert.Equal(PulumiEngineEventMapper.ApplyCompletedMessage, events[2].Message);
    }

    [Fact]
    public void Cancel_does_not_also_emit_resource_events_from_the_same_engine_event()
    {
        var mapped = PulumiEngineEventMapper.Map(
            Guid.NewGuid(),
            new PulumiEngineEventFacts(
                Sequence: 4,
                Cancelled: true,
                PreResourceType: "azure-native:app:ContainerApp",
                OutputsResourceType: "azure-native:app:ContainerApp",
                FailedResourceType: "azure-native:app:ContainerApp",
                DiagnosticError: true,
                Summary: true),
            DateTimeOffset.UtcNow).ToArray();

        Assert.Single(mapped);
        Assert.Equal(ProvisioningEventKind.OperationCancelled, mapped[0].Kind);
        Assert.Equal(PulumiEngineEventMapper.UpdateCancelledMessage, mapped[0].Message);
    }
}
