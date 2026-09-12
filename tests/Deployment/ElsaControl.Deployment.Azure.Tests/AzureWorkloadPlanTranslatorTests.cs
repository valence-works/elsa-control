using ElsaControl.Deployment.Azure;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureWorkloadPlanTranslatorTests
{
    internal const string ManifestDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string ImageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Rejects_missing_inputs_without_throwing()
    {
        var missingPlan = AzureWorkloadPlanTranslator.Translate(null, new("workload-a", "westeurope"));
        var missingTarget = AzureWorkloadPlanTranslator.Translate(CreatePlan(), null);

        Assert.Contains(missingPlan.Findings, x => x.Code == "plan.required");
        Assert.Contains(missingTarget.Findings, x => x.Code == "azure.target.required");
        Assert.Null(missingPlan.Plan);
        Assert.Null(missingTarget.Plan);
    }

    [Fact]
    public void Translates_supported_plan_to_deterministic_safe_intent()
    {
        var plan = CreatePlan();
        var target = new AzureWorkloadTarget("workload-a", "westeurope");

        var first = AzureWorkloadPlanTranslator.Translate(plan, target);
        var second = AzureWorkloadPlanTranslator.Translate(plan with
        {
            Evidence = plan.Evidence.Reverse().ToArray(),
            ProviderCapabilities = plan.ProviderCapabilities.Reverse().ToArray()
        }, target);

        Assert.True(first.IsAccepted);
        Assert.Empty(first.Findings);
        Assert.NotNull(first.Plan);
        Assert.Equal(first.Plan.Fingerprint, second.Plan?.Fingerprint);
        Assert.Equal("workload-a", first.Plan.WorkloadName);
        Assert.Equal("westeurope", first.Plan.Location);
        Assert.Equal("3.8.0-preview.5413", first.Plan.ElsaVersion);
        Assert.Equal("combined", first.Plan.Topology);
        Assert.Equal("Dedicated", first.Plan.Isolation);
        Assert.Equal("valenceruntimeimages.azurecr.io/runtime-combined", first.Plan.ImageRepository);
        Assert.Equal(new string('a', 64), first.Plan.ImageDigest);
        Assert.Equal("oci://release-manifest.example/manifest", first.Plan.ReleaseManifestReference);
        Assert.Equal(ManifestDigest, first.Plan.ReleaseManifestDigest);
        Assert.Equal("oci://release-manifest.example/signature", first.Plan.ReleaseManifestSignatureReference);
        Assert.Equal(ImageDigest, first.Plan.ReleaseManifestSignatureDigest);
        Assert.Equal("secret://vault/database-connection", first.Plan.SecretReferences["Database:ConnectionString"]);
        Assert.Equal("secret://vault/database-connection", first.Plan.SecretReferences["database:connectionstring"]);
        Assert.Matches("^[a-f0-9]{64}$", first.Plan.Fingerprint);
    }

    [Fact]
    public void Surfaces_resolved_plan_validation_and_does_not_translate()
    {
        var plan = CreatePlan() with
        {
            Topology = CreatePlan().Topology with
            {
                Components = [CreatePlan().Topology.Components[0] with
                {
                    Image = CreatePlan().Topology.Components[0].Image with
                    {
                        Reference = "valenceruntimeimages.azurecr.io/runtime-combined:latest"
                    }
                }]
            },
            Configuration = new([new("Database:ConnectionString", "string", true, true, false, null, Json("\"Server=secret\""), null, null)])
        };

        var result = AzureWorkloadPlanTranslator.Translate(plan, new("workload-a", "westeurope"));

        Assert.False(result.IsAccepted);
        Assert.Null(result.Plan);
        Assert.Contains(result.Findings, x => x.Code == "image.reference.immutableRequired");
        Assert.Contains(result.Findings, x => x.Code == "configuration.secretValue.forbidden");
        Assert.DoesNotContain(result.Findings, x => x.Message.Contains("Server=secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_arbitrary_extension_before_Azure_translation()
    {
        var plan = CreatePlan();
        var unsafePlan = plan with
        {
            Packages =
            [
                plan.Packages[0] with
                {
                    PackageId = "Customer.SecretPackage",
                    ExtensionClass = ResolvedExtensionClass.ArbitraryCustomer
                }
            ]
        };

        var result = AzureWorkloadPlanTranslator.Translate(unsafePlan, new("workload-a", "westeurope"));

        Assert.False(result.IsAccepted);
        Assert.Null(result.Plan);
        var finding = Assert.Single(result.Findings, candidate => candidate.Code == "package.extensionClass.forbidden");
        Assert.DoesNotContain("Customer.SecretPackage", System.Text.Json.JsonSerializer.Serialize(finding), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_floating_extension_version_before_Azure_translation()
    {
        var plan = CreatePlan();
        var unsafePlan = plan with
        {
            Packages = [plan.Packages[0] with { Version = "1.*" }]
        };

        var result = AzureWorkloadPlanTranslator.Translate(unsafePlan, new("workload-a", "westeurope"));

        Assert.False(result.IsAccepted);
        Assert.Null(result.Plan);
        Assert.Contains(result.Findings, candidate => candidate.Code == "package.version.inexact");
    }

    [Fact]
    public void Rejects_null_collections_without_throwing()
    {
        var result = AzureWorkloadPlanTranslator.Translate(
            CreatePlan() with { Packages = null! },
            new("workload-a", "westeurope"));

        Assert.False(result.IsAccepted);
        Assert.Null(result.Plan);
        Assert.Contains(result.Findings, x => x.Code == "azure.plan.normalization.invalid");
    }

    [Fact]
    public void Requires_exact_provider_package_metadata_before_translation()
    {
        var plan = CreatePlan();
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Release = plan.Release with
                {
                    ComponentDeclarations = plan.Release.ComponentDeclarations! with
                    {
                        Packages = [plan.Release.ComponentDeclarations.Packages[0]]
                    }
                }
            },
            new("workload-a", "westeurope"));

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Findings, x => x.Code == "azure.packageMetadata.required");
    }

    [Fact]
    public void Rejects_provider_package_metadata_that_is_not_an_exact_NuGet_version()
    {
        var plan = CreatePlan();
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Release = plan.Release with
                {
                    ComponentDeclarations = plan.Release.ComponentDeclarations! with
                    {
                        Packages = plan.Release.ComponentDeclarations.Packages.Select(package =>
                            string.Equals(package.Id, AzureWorkloadPlanTranslator.SqlWorkflowPackageId,
                                StringComparison.OrdinalIgnoreCase)
                                ? package with { Version = "3.8.0] || injected" }
                                : package).ToArray()
                    }
                }
            },
            new("workload-a", "westeurope"));

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Findings, x => x.Code == "azure.packageMetadata.invalid");
    }

    [Fact]
    public void Requires_the_secret_references_used_by_the_workload_template()
    {
        var result = AzureWorkloadPlanTranslator.Translate(
            CreatePlan() with
            {
                Configuration = new([new("Database:ConnectionString", "string", true, true, false,
                    "ELSA_DATABASE_CONNECTION", null, "secret://vault/database-connection", null)])
            },
            new("workload-a", "westeurope"));

        Assert.False(result.IsAccepted);
        Assert.Equal(2, result.Findings.Count(x => x.Code == "azure.secret.required"));
    }

    [Fact]
    public void Does_not_layer_provider_findings_over_base_schema_failures()
    {
        var result = AzureWorkloadPlanTranslator.Translate(
            CreatePlan() with { Topology = null! },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "topology.required");
        Assert.DoesNotContain(result.Findings, x => x.Code.StartsWith("azure.", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_key_casing_does_not_change_fingerprint()
    {
        var plan = CreatePlan();
        var changedCasing = plan with
        {
            Configuration = plan.Configuration with
            {
                Entries = [plan.Configuration.Entries[0] with { Key = "database:connectionstring" }, .. plan.Configuration.Entries.Skip(1)]
            }
        };

        var first = AzureWorkloadPlanTranslator.Translate(plan, new("workload-a", "westeurope"));
        var second = AzureWorkloadPlanTranslator.Translate(changedCasing, new("workload-a", "westeurope"));

        Assert.Equal(first.Plan?.Fingerprint, second.Plan?.Fingerprint);
    }

    [Fact]
    public void Source_commit_is_bound_into_the_provider_plan_fingerprint()
    {
        var firstPlan = CreatePlan();
        var secondPlan = firstPlan with
        {
            Release = firstPlan.Release with { SourceCommit = new string('f', 40) }
        };

        var first = AzureWorkloadPlanTranslator.Translate(firstPlan, new("workload-a", "westeurope"));
        var second = AzureWorkloadPlanTranslator.Translate(secondPlan, new("workload-a", "westeurope"));

        Assert.True(first.IsAccepted);
        Assert.True(second.IsAccepted);
        Assert.NotEqual(first.Plan?.Fingerprint, second.Plan?.Fingerprint);
    }

    [Theory]
    [InlineData("standard-small")]
    [InlineData("standard")]
    public void Carries_the_governed_capacity_of_the_workload_component(string profile)
    {
        var governed = ElsaInstancePlanResolutionOptions.Default.EffectiveCapacityProfiles[profile];

        var result = Translate(WithCapacity(governed.MinReplicas, governed.MaxReplicas, governed.CpuMillicores, governed.MemoryMiB, governed.EphemeralStorageMiB));

        Assert.True(result.IsAccepted);
        Assert.Equal(new AzureWorkloadCapacity(governed.MinReplicas, governed.MaxReplicas, governed.CpuMillicores, governed.MemoryMiB), result.Plan!.Capacity);
    }

    [Fact]
    public void Capacity_is_bound_into_the_provider_plan_fingerprint()
    {
        var small = Translate(WithCapacity(1, 1, 500, 1024));
        var scaled = Translate(WithCapacity(1, 3, 500, 1024));
        var larger = Translate(WithCapacity(1, 1, 1000, 2048));

        Assert.NotEqual(small.Plan!.Fingerprint, scaled.Plan!.Fingerprint);
        Assert.NotEqual(small.Plan.Fingerprint, larger.Plan!.Fingerprint);
    }

    [Theory]
    [InlineData(1, 1, 500, 2048, null)]
    [InlineData(1, 1, 300, 600, null)]
    [InlineData(1, 1, 4000, 8192, null)]
    [InlineData(0, 0, 500, 1024, null)]
    [InlineData(1, 301, 500, 1024, null)]
    [InlineData(1, 1, 500, 1024, 2049)]
    public void Rejects_capacity_without_an_exact_Container_Apps_consumption_mapping(
        int minReplicas, int maxReplicas, int cpuMillicores, int memoryMiB, int? ephemeralStorageMiB)
    {
        var result = Translate(WithCapacity(minReplicas, maxReplicas, cpuMillicores, memoryMiB, ephemeralStorageMiB));

        Assert.False(result.IsAccepted);
        Assert.Null(result.Plan);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("azure.capacity.unsupported", finding.Code);
        Assert.Equal("capacity:runtime", finding.Scope);
    }

    [Theory]
    [InlineData(true, 1, 1, true)]
    [InlineData(false, 1, 1, false)]
    [InlineData(true, 1, 3, false)]
    [InlineData(true, 0, 1, false)]
    public void Configures_the_managed_handoff_only_for_a_declaring_release_on_one_always_running_replica(
        bool declared, int minReplicas, int maxReplicas, bool expected)
    {
        var result = Translate(WithManagedHandoffCapability(declared, WithCapacity(minReplicas, maxReplicas, 500, 1024)));

        Assert.True(result.IsAccepted);
        Assert.Equal(expected, result.Plan!.ManagedHandoff);
    }

    [Fact]
    public void Managed_handoff_requires_the_exact_versioned_capability()
    {
        var plan = CreatePlan();
        var lookalike = plan with
        {
            Topology = plan.Topology with
            {
                Components = [plan.Topology.Components[0] with { Capabilities = ["Managed-Elsa-Handoff-V1", "managed-elsa-handoff-v2"] }]
            }
        };

        Assert.False(Translate(lookalike).Plan!.ManagedHandoff);
    }

    [Fact]
    public void Managed_handoff_is_bound_into_the_provider_plan_fingerprint()
    {
        var plain = Translate(CreatePlan());
        var handoff = Translate(WithManagedHandoffCapability(true, CreatePlan()));

        Assert.NotEqual(plain.Plan!.Fingerprint, handoff.Plan!.Fingerprint);
        Assert.Equal(handoff.Plan.Fingerprint, Translate(WithManagedHandoffCapability(true, CreatePlan())).Plan!.Fingerprint);
    }

    [Fact]
    public void Rejects_a_plan_without_capacity_for_the_workload_component()
    {
        var plan = CreatePlan();

        var result = Translate(plan with { Capacity = plan.Capacity with { Components = [] } });

        Assert.Null(result.Plan);
        Assert.Contains(result.Findings, x => x.Code == "azure.capacity.required" && x.Scope == "capacity:runtime");
    }

    [Fact]
    public void Manifest_payload_digest_is_admission_evidence_not_a_second_Azure_intent_identity()
    {
        var plan = CreatePlan();
        var firstPlan = plan with
        {
            Evidence = plan.Evidence.Select(x => x.Kind == ReleaseManifestEvidenceKinds.Manifest
                ? x with { PayloadDigest = "sha256:" + new string('c', 64) }
                : x).ToArray()
        };
        var secondPlan = plan with
        {
            Evidence = plan.Evidence.Select(x => x.Kind == ReleaseManifestEvidenceKinds.Manifest
                ? x with { PayloadDigest = "sha256:" + new string('d', 64) }
                : x).ToArray()
        };

        var first = AzureWorkloadPlanTranslator.Translate(firstPlan, new("workload-a", "westeurope"));
        var second = AzureWorkloadPlanTranslator.Translate(secondPlan, new("workload-a", "westeurope"));

        Assert.True(first.IsAccepted);
        Assert.True(second.IsAccepted);
        Assert.Equal(first.Plan?.Fingerprint, second.Plan?.Fingerprint);
        Assert.Equal(ManifestDigest, first.Plan?.ReleaseManifestDigest);
    }

    [Fact]
    public void Equivalent_source_commit_whitespace_does_not_change_the_provider_plan_fingerprint()
    {
        var plan = CreatePlan();
        var first = AzureWorkloadPlanTranslator.Translate(plan, new("workload-a", "westeurope"));
        var second = AzureWorkloadPlanTranslator.Translate(
            plan with { Release = plan.Release with { SourceCommit = $" {plan.Release.SourceCommit.ToUpperInvariant()} " } },
            new("workload-a", "westeurope"));

        Assert.Equal(first.Plan?.Fingerprint, second.Plan?.Fingerprint);
    }

    [Fact]
    public void Rejects_null_image_repository_without_throwing()
    {
        var plan = CreatePlan();
        var component = plan.Topology.Components[0];
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Topology = plan.Topology with
                {
                    Components = [component with { Image = component.Image with { Repository = null! } }]
                }
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "image.repository.required");
        Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData("server-studio", "Dedicated", "westeurope", "azure.topology.unsupported")]
    [InlineData("combined", "Shared", "westeurope", "azure.isolation.unsupported")]
    [InlineData("combined", "Dedicated", "eastus", "azure.location.unsupported")]
    public void Rejects_unsupported_initial_provider_profile(
        string topology,
        string isolation,
        string location,
        string expectedCode)
    {
        var plan = CreatePlan() with
        {
            Topology = CreatePlan().Topology with { Id = topology },
            Isolation = isolation
        };

        var result = AzureWorkloadPlanTranslator.Translate(plan, new("workload-a", location));

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Findings, x => x.Code == expectedCode);
    }

    [Theory]
    [InlineData("westeurope")]
    [InlineData("northeurope")]
    [InlineData("swedencentral")]
    public void Accepts_each_governed_proof_location(string location)
    {
        var result = AzureWorkloadPlanTranslator.Translate(CreatePlan(), new("workload-a", location));

        Assert.True(result.IsAccepted);
        Assert.Equal(location, result.Plan?.Location);
    }

    [Fact]
    public void Rejects_private_networking_and_unknown_required_capabilities()
    {
        var plan = CreatePlan();
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Network = plan.Network with { Egress = "restricted", RequiresPrivateConnectivity = true },
                ProviderCapabilities = [.. plan.ProviderCapabilities, new("gpu-runtime", "Needs GPU compute.", true, ["gpu"])]
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.network.unsupported");
        Assert.Contains(result.Findings, x => x.Code == "azure.providerCapability.unsupported");
    }

    [Fact]
    public void Rejects_missing_or_inconsistent_release_manifest_evidence()
    {
        var missing = AzureWorkloadPlanTranslator.Translate(
            CreatePlan() with { Evidence = [] },
            new("workload-a", "westeurope"));
        var mismatch = AzureWorkloadPlanTranslator.Translate(
            CreatePlan() with { Evidence = [new(ReleaseManifestEvidenceKinds.Manifest, "oci://other", ManifestDigest, "Verified release manifest")] },
            new("workload-a", "westeurope"));

        Assert.Contains(missing.Findings, x => x.Code == "azure.releaseManifestEvidence.required");
        Assert.Contains(mismatch.Findings, x => x.Code == "azure.releaseManifestEvidence.mismatch");
    }

    [Fact]
    public void Rejects_missing_or_unsafe_signature_evidence()
    {
        var missing = CreatePlan() with
        {
            Evidence = CreatePlan().Evidence.Where(x => x.Kind != ReleaseManifestEvidenceKinds.Signature).ToArray()
        };
        var unsafeEvidence = CreatePlan().Evidence
            .Select(x => x.Kind == ReleaseManifestEvidenceKinds.Signature ? x with { Reference = "https://user:token@example.com/signature?token=secret" } : x)
            .ToArray();

        var missingResult = AzureWorkloadPlanTranslator.Translate(missing, new("workload-a", "westeurope"));
        var unsafeResult = AzureWorkloadPlanTranslator.Translate(CreatePlan() with { Evidence = unsafeEvidence }, new("workload-a", "westeurope"));

        Assert.Contains(missingResult.Findings, x => x.Code == "azure.releaseManifestSignatureEvidence.required");
        Assert.Contains(unsafeResult.Findings, x => x.Code == "azure.releaseManifestSignatureEvidence.invalid");
        Assert.DoesNotContain(unsafeResult.Findings, x => x.Message.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_image_reference_digest_mismatch()
    {
        var component = CreatePlan().Topology.Components[0];
        var result = AzureWorkloadPlanTranslator.Translate(
            CreatePlan() with
            {
                Topology = new("combined", [component with { Image = component.Image with { Digest = ManifestDigest } }])
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "image.referenceDigest.mismatch");
    }

    [Fact]
    public void Rejects_image_repository_that_disagrees_with_immutable_reference()
    {
        var plan = CreatePlan();
        var component = plan.Topology.Components[0];
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Topology = plan.Topology with
                {
                    Components = [component with
                    {
                        Image = component.Image with { Repository = "valenceruntimeimages.azurecr.io/other" }
                    }]
                }
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.imageReference.repositoryMismatch");
    }

    [Fact]
    public void Rejects_other_repository_under_the_governed_registry()
    {
        var plan = CreatePlan();
        var component = plan.Topology.Components[0];
        const string repository = "valenceruntimeimages.azurecr.io/runtime-server";
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Topology = plan.Topology with
                {
                    Components = [component with
                    {
                        Image = component.Image with
                        {
                            Repository = repository,
                            Reference = $"{repository}@{ImageDigest}"
                        }
                    }]
                }
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.imageRepository.invalid");
    }

    [Fact]
    public void Rejects_images_outside_initial_paid_registry_authority()
    {
        var plan = CreatePlan();
        var component = plan.Topology.Components[0];
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Topology = plan.Topology with
                {
                    Components = [component with
                    {
                        Image = component.Image with
                        {
                            RegistryClass = "community",
                            Repository = "ghcr.io/example/runtime",
                            Reference = $"ghcr.io/example/runtime@{ImageDigest}"
                        }
                    }]
                }
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.imageRegistry.unsupported");
    }

    [Fact]
    public void Rejects_public_endpoint_without_Https_and_Tls()
    {
        var plan = CreatePlan();
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Network = plan.Network with
                {
                    Endpoints = [plan.Network.Endpoints[0] with { Protocol = "http", RequiresTls = false }]
                }
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.network.tlsRequired");
    }

    [Fact]
    public void Rejects_unsafe_image_repository_and_manifest_locator_without_echoing_them()
    {
        var plan = CreatePlan();
        var component = plan.Topology.Components[0];
        const string unsafeRepository = "user:secret@registry.example/runtime";
        const string unsafeManifest = "https://user:secret@example.com/manifest?token=secret";
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Release = plan.Release with { ReleaseManifestReference = unsafeManifest },
                Topology = plan.Topology with
                {
                    Components = [component with
                    {
                        Image = component.Image with
                        {
                            Repository = unsafeRepository,
                            Reference = $"{unsafeRepository}@{ImageDigest}"
                        }
                    }]
                },
                Evidence = plan.Evidence.Select(x => x.Kind == ReleaseManifestEvidenceKinds.Manifest ? x with { Reference = unsafeManifest } : x).ToArray()
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.imageRepository.invalid");
        Assert.Contains(result.Findings, x => x.Code == "azure.releaseManifestEvidence.mismatch");
        Assert.DoesNotContain(result.Findings, x => x.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("valenceruntimeimages.azurecr.io/../runtime")]
    [InlineData("valenceruntimeimages.azurecr.io/-runtime")]
    [InlineData("valenceruntimeimages.azurecr.io/runtime/")]
    [InlineData("valenceruntimeimages.azurecr.io/runtime//child")]
    public void Rejects_non_Oci_repository_paths(string repository)
    {
        var plan = CreatePlan();
        var component = plan.Topology.Components[0];
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Topology = plan.Topology with
                {
                    Components = [component with
                    {
                        Image = component.Image with
                        {
                            Repository = repository,
                            Reference = $"{repository}@{ImageDigest}"
                        }
                    }]
                }
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.imageRepository.invalid");
    }

    [Fact]
    public void Rejects_repository_names_over_the_Oci_length_limit()
    {
        var plan = CreatePlan();
        var component = plan.Topology.Components[0];
        var repository = $"valenceruntimeimages.azurecr.io/{new string('a', 256)}";
        var result = AzureWorkloadPlanTranslator.Translate(
            plan with
            {
                Topology = plan.Topology with
                {
                    Components = [component with
                    {
                        Image = component.Image with { Repository = repository, Reference = $"{repository}@{ImageDigest}" }
                    }]
                }
            },
            new("workload-a", "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.imageRepository.invalid");
    }

    [Theory]
    [InlineData("--bad")]
    [InlineData("bad-")]
    [InlineData("this-name-is-far-too-long")]
    public void Rejects_workload_names_that_cannot_be_Bicep_inputs(string name)
    {
        var result = AzureWorkloadPlanTranslator.Translate(CreatePlan(), new(name, "westeurope"));

        Assert.Contains(result.Findings, x => x.Code == "azure.workloadName.invalid");
    }

    [Fact]
    public void Later_Elsa_version_remains_data_and_is_accepted_when_admitted()
    {
        var plan = CreatePlan("5.0", "5.0.0") with
        {
            Release = CreatePlan("5.0", "5.0.0").Release with
            {
                ComponentDeclarations = new(
                    "central-package-declarations-v1",
                    ImageDigest,
                    [
                        new(AzureWorkloadPlanTranslator.SqlWorkflowPackageId, "5.0.1"),
                        new(AzureWorkloadPlanTranslator.SqlQuartzPackageId, "5.0.2")
                    ])
            }
        };

        var result = AzureWorkloadPlanTranslator.Translate(plan, new("workload-a", "westeurope"));

        Assert.True(result.IsAccepted);
        Assert.Empty(result.Findings);
        Assert.Equal("5.0", result.Plan?.ReleaseLine);
        Assert.Equal("5.0.0", result.Plan?.ElsaVersion);
    }

    internal static ResolvedElsaApplicationPlan CreatePlan(string releaseLine = "3.8", string version = "3.8.0-preview.5413")
    {
        var component = new ResolvedElsaComponent(
            "runtime",
            ["studio", "server"],
            new("paid", "valenceruntimeimages.azurecr.io/runtime-combined", $"valenceruntimeimages.azurecr.io/runtime-combined@{ImageDigest}", ImageDigest),
            ["elsa.studio", "elsa.server"],
            [new("studio", "https", 8080, "public", true, "/"), new("api", "https", 8080, "public", true, "/elsa/api")],
            ["workflow.runtime", "workflow.studio"]);

        return new(
            ResolvedElsaApplicationPlanSchema.CurrentVersion,
            new(
                "valence-runtime",
                releaseLine,
                version,
                "https://github.com/valence-works/elsa-production-image",
                "1aeee8df455b21cf3bf3d2b26dfbd512d76da27b",
                "oci://release-manifest.example/manifest",
                ManifestDigest,
                new(
                    "central-package-declarations-v1",
                    ImageDigest,
                    [
                        new(AzureWorkloadPlanTranslator.SqlWorkflowPackageId, "3.8.0-preview.5413"),
                        new(AzureWorkloadPlanTranslator.SqlQuartzPackageId, "3.8.0-preview.342")
                    ])),
            new("combined", [component]),
            [
                new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Elsa.Core", version, ImageDigest, ["elsa.server"], [new("runtime", "Elsa.Runtime", ["elsa.server"], ["workflow.runtime"])], ResolvedExtensionClass.BuiltIn, ImageDigest),
                new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), AzureWorkloadPlanTranslator.SqlWorkflowPackageId, "3.8.0-preview.5413", ImageDigest, ["elsa.server"], [], ResolvedExtensionClass.BuiltIn, ImageDigest),
                new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), AzureWorkloadPlanTranslator.SqlQuartzPackageId, "3.8.0-preview.342", ImageDigest, ["elsa.server"], [], ResolvedExtensionClass.BuiltIn, ImageDigest)
            ],
            new([
                new("Database:ConnectionString", "string", true, true, false, "ELSA_DATABASE_CONNECTION", null, "secret://vault/database-connection", null),
                new("Identity:SigningKey", "string", true, true, false, "ELSA_IDENTITY_SIGNING_KEY", null, "secret://vault/identity-signing-key", null),
                new("Admin:Password", "string", true, true, false, "ELSA_ADMIN_PASSWORD", null, "secret://vault/admin-password", null)
            ]),
            new([new("runtime", 1, 1, 500, 1024)], [new("elsa-data", "relational", "persistent", "exclusive", 10)]),
            new("public", "unrestricted", false, [], [new("runtime", "api", "https", 443, "public", true, "/elsa/api")]),
            "Dedicated",
            new("preview", "Preview", "internal", "automatic-within-minor", "explicit-approval", "explicit-migration"),
            [new("managed-runtime", "Run the resolved runtime components.", true, ["container", "persistent-storage"])],
            [
                new(ReleaseManifestEvidenceKinds.Manifest, "oci://release-manifest.example/manifest", ManifestDigest, "Verified release manifest"),
                new(ReleaseManifestEvidenceKinds.Signature, "oci://release-manifest.example/signature", ImageDigest, "Verified release manifest signature"),
                new("catalog", "catalog://snapshot", null, "Resolved catalog snapshot")
            ]);
    }

    private static AzureWorkloadPlanTranslation Translate(ResolvedElsaApplicationPlan plan) =>
        AzureWorkloadPlanTranslator.Translate(plan, new("workload-a", "westeurope"));

    private static ResolvedElsaApplicationPlan WithCapacity(
        int minReplicas, int maxReplicas, int cpuMillicores, int memoryMiB, int? ephemeralStorageMiB = null)
    {
        var plan = CreatePlan();
        return plan with
        {
            Capacity = plan.Capacity with
            {
                Components = [new("runtime", minReplicas, maxReplicas, cpuMillicores, memoryMiB, ephemeralStorageMiB)]
            }
        };
    }

    private static ResolvedElsaApplicationPlan WithManagedHandoffCapability(bool declared, ResolvedElsaApplicationPlan plan)
    {
        var component = plan.Topology.Components[0];
        return plan with
        {
            Topology = plan.Topology with
            {
                Components =
                [
                    component with
                    {
                        Capabilities = declared
                            ? [.. component.Capabilities, ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV1]
                            : component.Capabilities
                    }
                ]
            }
        };
    }

    private static System.Text.Json.JsonElement Json(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
