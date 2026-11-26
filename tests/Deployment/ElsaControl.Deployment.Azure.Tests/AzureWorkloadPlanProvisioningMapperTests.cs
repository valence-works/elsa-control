using ElsaControl.Deployment.Azure;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureWorkloadPlanProvisioningMapperTests
{
    [Fact]
    public void Maps_fingerprint_capacity_and_immutable_engine_stack_name()
    {
        var instanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var plan = Plan();
        var config = AzureWorkloadPlanProvisioningMapper.StackConfig(
            plan,
            instanceId,
            AzureWorkloadPlanProvisioningMapper.PlatformStackReference("staging"),
            organizationSlug: "acme",
            engineSlug: "prod",
            capacity: new AzureWorkloadCapacity(0, 1, 500, 1024));

        Assert.Equal("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", AzureWorkloadPlanProvisioningMapper.EngineStackName(instanceId));
        Assert.Equal("organization/elsa-platform/staging", config["platformStack"]);
        Assert.Equal(plan.Fingerprint, config["planFingerprint"]);
        Assert.Equal(plan.ImageDigest, config["imageDigest"]);
        Assert.Equal(plan.ImageRepository, config["imageRepository"]);
        Assert.Equal("Dedicated", config["isolation"]);
        Assert.Equal("combined", config["topology"]);
        Assert.Equal("westeurope", config["location"]);
        Assert.Equal("0.5", config["cpu"]);
        Assert.Equal("1Gi", config["memory"]);
        Assert.Equal("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", config["resourceGroupName"]);
        Assert.Equal("acme", config["organizationSlug"]);
        Assert.Equal("prod", config["engineSlug"]);
        Assert.Equal("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", config["engineName"]);
        Assert.DoesNotContain("acme", AzureWorkloadPlanProvisioningMapper.EngineStackName(instanceId), StringComparison.Ordinal);
        Assert.Equal(plan.Fingerprint[..24], config["revisionSuffix"]);
        foreach (var pair in config)
        {
            Assert.False(ProvisioningEventSanitizer.ContainsSecretOrResourceIdentifier(pair.Key));
            Assert.False(ProvisioningEventSanitizer.ContainsSecretOrResourceIdentifier(pair.Value));
        }
    }

    [Fact]
    public void Org_and_slug_are_tags_not_the_stack_name()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var one = AzureWorkloadPlanProvisioningMapper.StackConfig(
            Plan(),
            first,
            AzureWorkloadPlanProvisioningMapper.PlatformStackReference("staging"),
            organizationSlug: "org/slug",
            engineSlug: "one");
        var two = AzureWorkloadPlanProvisioningMapper.StackConfig(
            Plan(),
            second,
            AzureWorkloadPlanProvisioningMapper.PlatformStackReference("staging"),
            organizationSlug: "org-slug",
            engineSlug: "two");
        Assert.NotEqual(
            AzureWorkloadPlanProvisioningMapper.EngineStackName(first),
            AzureWorkloadPlanProvisioningMapper.EngineStackName(second));
        Assert.Equal("org/slug", one["organizationSlug"]);
        Assert.Equal("org-slug", two["organizationSlug"]);
        Assert.Equal(ProvisioningStackName.Canonicalize("org/slug"), ProvisioningStackName.Canonicalize("org-slug"));
    }

    [Fact]
    public void Handoff_stays_off_unless_control_configured_it()
    {
        var instanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var declared = AzureWorkloadPlanProvisioningMapper.StackConfig(
            Plan() with { ManagedHandoff = true },
            instanceId,
            "organization/elsa-platform/staging",
            capacity: new AzureWorkloadCapacity(1, 1, 500, 1024),
            handoffConfigured: false);
        Assert.Equal("true", declared["managedHandoff"]);
        Assert.Equal("false", declared["handoffConfigured"]);
        var configured = AzureWorkloadPlanProvisioningMapper.StackConfig(
            Plan() with { ManagedHandoff = true },
            instanceId,
            "organization/elsa-platform/staging",
            capacity: new AzureWorkloadCapacity(1, 1, 500, 1024),
            handoffConfigured: true);
        Assert.Equal("true", configured["handoffConfigured"]);
        Assert.Equal("1", configured["minReplicas"]);
        Assert.Equal("1", configured["maxReplicas"]);
    }

    private static AzureWorkloadPlan Plan() =>
        new(
            "workload-a",
            "westeurope",
            "3.8",
            "3.8",
            "combined",
            "Dedicated",
            "valenceruntimeimages.azurecr.io/runtime-combined",
            new string('a', 64),
            "oci://release-manifest.example/manifest",
            "sha256:" + new string('b', 64),
            "oci://release-manifest.example/signature",
            "sha256:" + new string('c', 64),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["database:connectionstring"] = "secret://vault/database-connection",
                ["identity:signingkey"] = "secret://vault/signing-key",
                ["admin:password"] = "secret://vault/admin-password"
            },
            new string('d', 64),
            SqlWorkflowPackageVersion: "1.0.0",
            SqlQuartzPackageVersion: "1.0.0",
            Capacity: new AzureWorkloadCapacity(0, 1, 500, 1024),
            ManagedHandoff: false);
}
