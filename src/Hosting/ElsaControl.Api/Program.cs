using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ConsoleLogStreaming.AspNetCore.DependencyInjection;
using ConsoleLogStreaming.Core.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using ElsaControl.Deployment.Artifacts;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Abstractions.Catalog;
using ElsaControl.PackageCatalog.Abstractions.Compatibility;
using ElsaControl.Api.Admin.Application;
using ElsaControl.Api.Admin.Organizations;
using ElsaControl.Api.Admin.Workspaces;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Catalog;
using ElsaControl.Api.Admin.Packages;
using ElsaControl.Api.Admin.Sources;
using ElsaControl.Api.Admin.Sync;
using ElsaControl.Api.Admin.ReleaseCatalog;
using ElsaControl.Api.Public.Builder;
using ElsaControl.Api.Public.Compatibility;
using ElsaControl.Api.Public.Features;
using ElsaControl.Api.Public.Packages;
using ElsaControl.Api.Public.Sources;
using ElsaControl.Api.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.Billing.Stripe;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.PackageCatalog.Core.Approvals;
using ElsaControl.RuntimeBuilder.DeploymentTemplates;
using ElsaControl.PackageCatalog.Core.Compatibility;
using ElsaControl.PackageCatalog.Core.Manifests;
using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Persistence;
using ElsaControl.RuntimeBuilder.Abstractions.RuntimeConfigurations;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.PackageCatalog.Core.Sources;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Sources.NuGet;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Elsa.Specifications.PackageManifests.Validation;
using ElsaControl.RuntimeBuilder.Core.Builder;
using ElsaControl.RuntimeBuilder.Core.Builder.Planner;
using ElsaControl.RuntimeBuilder.Core.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;
using ElsaControl.RuntimeBuilder.Core.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Core.ReleaseManifests;
using ElsaControl.Api.ReleaseCatalog;
using ElsaControl.Api.Telemetry;
using ElsaControl.Weaver.Core.Configuration;
using ElsaControl.Weaver.Core.Plans;
using ElsaControl.Weaver.Core.Runtime;
using ElsaControl.Weaver.Core.Safety;
using ElsaControl.Weaver.Core.Sessions;
using ElsaControl.Weaver.Core.Tools;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using ElsaControl.Api;
using StripeClient = Stripe.StripeClient;

var builder = WebApplication.CreateBuilder(args);

IdentityModelEventSource.ShowPII = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Diagnostics:IdentityModel:ShowPII");

builder.AddServiceDefaults();
builder.AddManagedLifecycleAzureMonitorTelemetry();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(AdminDashboardAuthenticationDefaults.DevelopmentUrlConfigurationKey);
builder.Services.AddConsoleLogStreamingHost(options =>
{
    options.ServiceName = "Elsa Control API";
    options.SourceId = "elsa-control-api";
    options.SourceDisplayName = "Elsa Control API";
    options.RecentCapacity = 5_000;
    options.MaxRecentQuerySize = 1_000;
});
builder.Services.AddConsoleLogStreamingAspNetCore(options =>
{
    options.AuthorizationPolicy = AdminAuthorization.Policy;
    options.RecentPath = "/api/admin/console-logs/recent";
    options.SourcesPath = "/api/admin/console-logs/sources";
    options.HubPath = "/api/admin/console-logs/hub";
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
});
builder.Services.Configure<ControlIdentityOptions>(builder.Configuration.GetSection(ControlIdentityDefaults.ConfigurationSection));
builder.Services.Configure<ManagedElsaHandoffOptions>(builder.Configuration.GetSection(ManagedElsaHandoffDefaults.ConfigurationSection));
var configuredControlIdentity = builder.Configuration.GetSection(ControlIdentityDefaults.ConfigurationSection).Get<ControlIdentityOptions>() ?? new ControlIdentityOptions();
var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = ControlIdentityDefaults.Scheme;
        options.DefaultChallengeScheme = ControlIdentityDefaults.Scheme;
    })
    .AddJwtBearer(ControlIdentityDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.Scheme, _ => { })
    .AddScheme<AuthenticationSchemeOptions, BuilderClientApiKeyAuthenticationHandler>(BuilderClientApiKeyAuthenticationDefaults.Scheme, _ => { })
    .AddCookie(CustomerAuthenticationDefaults.CookieScheme, options =>
    {
        options.Cookie.Name = CustomerAuthenticationDefaults.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsEnvironment("Testing")
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = CustomerAuthenticationDefaults.SessionLifetime;
        options.LoginPath = CustomerAuthenticationDefaults.LoginPath;
        options.LogoutPath = CustomerAuthenticationDefaults.LogoutPath;
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
if (configuredControlIdentity.IsCustomerLoginConfigured)
{
    authentication.AddOpenIdConnect(CustomerAuthenticationDefaults.OidcScheme, _ => { });
    builder.Services.AddOptions<OpenIdConnectOptions>(CustomerAuthenticationDefaults.OidcScheme)
        .Configure<IOptions<ControlIdentityOptions>>((options, controlIdentityOptions) =>
            CustomerOidcOptionsConfigurator.Configure(options, controlIdentityOptions.Value));
}
builder.Services.AddOptions<JwtBearerOptions>(ControlIdentityDefaults.Scheme)
    .Configure<IOptions<ControlIdentityOptions>>((options, controlIdentityOptions) =>
    {
        var controlIdentity = controlIdentityOptions.Value;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = controlIdentity.RequireHttpsMetadata;
        options.Authority = string.IsNullOrWhiteSpace(controlIdentity.Authority) ? null : controlIdentity.Authority;
        options.Audience = string.IsNullOrWhiteSpace(controlIdentity.Audience) ? null : controlIdentity.Audience;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(controlIdentity.Issuer)
                || !string.IsNullOrWhiteSpace(controlIdentity.Authority)
                || !string.IsNullOrWhiteSpace(controlIdentity.SymmetricSigningKey),
            ValidIssuer = string.IsNullOrWhiteSpace(controlIdentity.Issuer)
                ? (string.IsNullOrWhiteSpace(controlIdentity.Authority) ? null : controlIdentity.Authority)
                : controlIdentity.Issuer,
            ValidateAudience = true,
            ValidAudience = string.IsNullOrWhiteSpace(controlIdentity.Audience) ? null : controlIdentity.Audience,
            ValidateIssuerSigningKey = !string.IsNullOrWhiteSpace(controlIdentity.SymmetricSigningKey),
            IssuerSigningKey = string.IsNullOrWhiteSpace(controlIdentity.SymmetricSigningKey)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(controlIdentity.SymmetricSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
// The public Runtime Builder API is called from browsers on other origins (the Elsa Hub
// configurator), so those origins must be allow-listed. Everything else on this host is
// same-origin and needs no CORS.
var builderClientOrigins = builder.Configuration
    .GetSection(PublicBuilderCors.AllowedOriginsConfigurationKey)
    .Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(PublicBuilderCors.PolicyName, policy =>
{
    if (builderClientOrigins.Length == 0)
        return;

    policy.WithOrigins(builderClientOrigins)
        .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Options)
        .WithHeaders("Content-Type", ApiKeyAuthenticationDefaults.HeaderName);
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Managed Elsa identity handoff rate limit was exceeded.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "handoff.rate-limited",
                    ["correlationId"] = context.HttpContext.TraceIdentifier
                })
            .ExecuteAsync(context.HttpContext);
    };
    options.AddPolicy("managed-elsa-handoff", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddCatalogAuthorization();
builder.Services.AddBuilderClientAuthorization();
builder.Services.Configure<ReleaseCatalogAdmissionOptions>(
    builder.Configuration.GetSection(ReleaseCatalogAdmissionOptions.ConfigurationSection));
builder.Services.Configure<StripeBillingOptions>(
    builder.Configuration.GetSection(StripeBillingOptions.ConfigurationSection));
builder.Services.Configure<OrganizationBillingLifecycleWorkerOptions>(
    builder.Configuration.GetSection(OrganizationBillingLifecycleWorkerOptions.ConfigurationSection));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AdminApiKeyValidator>();
builder.Services.AddSingleton<BuilderClientApiKeyValidator>();
builder.Services.AddSingleton<ManagedElsaHandoffKeyRing>(services =>
{
    var options = services.GetRequiredService<IOptions<ManagedElsaHandoffOptions>>().Value;
    if (!options.Enabled)
        return ManagedElsaHandoffKeyRing.CreateEphemeral();
    var hasKeyId = !string.IsNullOrWhiteSpace(options.ActiveKeyId);
    var hasPrivateKey = !string.IsNullOrWhiteSpace(options.ActivePrivateKeyPem);
    if (hasKeyId != hasPrivateKey)
        throw new InvalidOperationException(
            "Managed Elsa handoff active signing key configuration must include both key ID and private key.");
    return hasKeyId
        ? ManagedElsaHandoffKeyRing.CreateConfigured(options)
        : ManagedElsaHandoffKeyRing.CreateEphemeral();
});
builder.Services.AddScoped<EfCoreManagedElsaHandoffStore>();
builder.Services.AddScoped<IManagedElsaHandoffReplayStore, EfCoreManagedElsaHandoffReplayStore>();
builder.Services.AddScoped<IManagedElsaInstanceCatalog, EfCoreManagedElsaInstanceCatalog>();
builder.Services.AddScoped<EfCoreManagedElsaInstanceIdentityStore>();
builder.Services.AddScoped<IManagedElsaInstanceIdentityStore>(services => new ControlHandoffGatedIdentityStore(
    services.GetRequiredService<EfCoreManagedElsaInstanceIdentityStore>(),
    services.GetRequiredService<IOptions<ManagedElsaHandoffOptions>>()));
builder.Services.AddScoped<IManagedElsaHandoffAuthorizer, ManagedElsaInstanceHandoffAuthorizer>();
builder.Services.AddScoped<IManagedElsaHandoffAuditSink, EfCoreManagedElsaHandoffAuditSink>();
builder.Services.AddScoped<ManagedElsaHandoffIssuer>();
builder.Services.AddScoped<ManagedElsaHandoffRedeemer>();
builder.Services.AddScoped<ManagedElsaHandoffService>();
builder.Services.AddHostedService<ManagedElsaHandoffConfigurationValidator>();
builder.Services.AddSingleton<IWorkspacePermissionContribution, ManagedElsaInstancePermissionContribution>();
var catalogSqlManagedIdentityInterceptor =
    CatalogSqlManagedIdentityConnectionInterceptor.TryCreate(builder.Configuration);
builder.Services.AddCatalogDbContext(builder.Configuration, options =>
{
    if (catalogSqlManagedIdentityInterceptor is not null)
        options.AddInterceptors(catalogSqlManagedIdentityInterceptor);
});
builder.Services.AddScoped<OrganizationBillingStore>();
builder.Services.AddScoped<IOrganizationBillingStore>(services =>
    services.GetRequiredService<OrganizationBillingStore>());
builder.Services.AddScoped<IOrganizationBillingLifecycleStore>(services =>
    services.GetRequiredService<OrganizationBillingStore>());
builder.Services.AddScoped<IOrganizationInternalEntitlementStore>(services =>
    services.GetRequiredService<OrganizationBillingStore>());
builder.Services.AddScoped<OrganizationInternalEntitlementService>();
builder.Services.AddScoped<OrganizationBillingLifecycleWorker>(services =>
    new OrganizationBillingLifecycleWorker(
        services.GetRequiredService<IOrganizationBillingLifecycleStore>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetService<IOrganizationBillingCleanupProvider>()));
builder.Services.AddScoped<OrganizationBillingService>();
builder.Services.AddScoped<OrganizationBillingApiService>();
builder.Services.AddSingleton<Func<StripeClient>>(services =>
{
    var options = services.GetRequiredService<IOptions<StripeBillingOptions>>().Value;
    return () =>
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.SecretKey))
            throw new BillingProviderUnavailableException("The Stripe billing provider is not configured.");
        return new StripeClient(options.SecretKey);
    };
});
builder.Services.AddScoped<IStripeCheckoutSessionGateway, StripeCheckoutSessionGateway>();
builder.Services.AddScoped<IStripeCustomerPortalGateway, StripeCustomerPortalGateway>();
builder.Services.AddScoped<IStripeSubscriptionCleanupGateway, StripeSubscriptionCleanupGateway>();
builder.Services.AddScoped<StripeBillingProvider>();
builder.Services.AddScoped<IBillingProvider>(services => services.GetRequiredService<StripeBillingProvider>());
builder.Services.AddScoped<IOrganizationBillingCleanupProvider>(services => services.GetRequiredService<StripeBillingProvider>());
builder.Services.AddScoped<EfCoreElsaInstanceLifecycleStore>();
builder.Services.AddScoped<IElsaInstanceCommercialGate, EfCoreElsaInstanceCommercialGate>();
builder.Services.AddScoped<IElsaInstanceLifecycleStore>(services => services.GetRequiredService<EfCoreElsaInstanceLifecycleStore>());
builder.Services.AddScoped<IElsaInstanceLifecycleWorkerStore>(services => services.GetRequiredService<EfCoreElsaInstanceLifecycleStore>());
builder.Services.AddScoped<IElsaInstanceProviderSubmissionStore>(services => services.GetRequiredService<EfCoreElsaInstanceLifecycleStore>());
builder.Services.AddScoped<IElsaInstanceEntitlementHoldStore>(services => services.GetRequiredService<EfCoreElsaInstanceLifecycleStore>());
builder.Services.AddScoped<IElsaInstanceProviderPendingOperationStore>(services => services.GetRequiredService<EfCoreElsaInstanceLifecycleStore>());
builder.Services.AddScoped<IElsaInstanceProviderReconciliationStore>(services => services.GetRequiredService<EfCoreElsaInstanceLifecycleStore>());
builder.Services.AddScoped<IElsaInstanceDeletionStore>(services => services.GetRequiredService<EfCoreElsaInstanceLifecycleStore>());
builder.Services.Configure<ElsaInstancePlanAuthorityOptions>(
    builder.Configuration.GetSection(ElsaInstancePlanAuthorityOptions.ConfigurationSection));
var azureInstanceLifecycleConfigured = builder.Configuration.GetValue<bool>(
    $"{AzureElsaInstanceProviderOptions.ConfigurationSection}:{nameof(AzureElsaInstanceProviderOptions.Enabled)}");
var governedAzureSecretReferences = azureInstanceLifecycleConfigured
    ? ConfiguredAzureSecretResolver.ReadNamedReferences(builder.Configuration, requireProviderOwnedCredentials: true)
    : null;
builder.Services.AddScoped<IElsaInstanceLifecycleResolutionInputSource>(services =>
    new CatalogElsaInstanceLifecycleResolutionInputSource(
        services.GetRequiredService<CatalogDbContext>(),
        services.GetRequiredService<IGovernedReleaseCatalogStore>(),
        services.GetRequiredService<IOptions<ElsaInstancePlanAuthorityOptions>>().Value,
        governedAzureSecretReferences));
ElsaInstancePlanResolutionComposition.AddResolver(builder.Services, builder.Configuration);
builder.Services.AddScoped<ElsaInstanceLifecycleService>();
builder.Services.AddScoped<ElsaInstanceLifecycleWorker>();
builder.Services.AddScoped<ElsaInstanceDeletionWorker>();
var azureProviderRunnerAuthority = AzureProviderRunnerComposition.AddRunner(
    builder.Services, builder.Configuration);
if (azureProviderRunnerAuthority is { } authority)
{
    builder.Services.AddSingleton<IAzureProviderAuthorityPreflight>(_ =>
        new AzureProviderAuthorityPreflight(authority.Options, authority.Scope));
}
var azureInstanceLifecycleEnabled = AzureInstanceLifecycleComposition.AddProviderPorts(
    builder.Services, builder.Configuration, azureProviderRunnerAuthority);
if (azureInstanceLifecycleEnabled)
{
    builder.Services.AddScoped<ElsaInstanceProviderReconciliationService>();
    builder.Services.AddScoped<IElsaInstanceProviderReconciliationService>(services =>
        services.GetRequiredService<ElsaInstanceProviderReconciliationService>());
}
builder.Services.AddScoped<IManagedElsaInstanceApiStore, EfCoreManagedElsaInstanceApiStore>();
builder.Services.AddScoped<IManagedElsaInstanceOperationalStore, EfCoreManagedElsaInstanceOperationalStore>();
builder.Services.Configure<ManagedLifecycleOperationalHealthOptions>(
    builder.Configuration.GetSection(ManagedLifecycleOperationalHealthOptions.ConfigurationSection));
builder.Services.AddSingleton(services => new ManagedLifecycleOperationalHealthEvaluator(
    services.GetRequiredService<IOptions<ManagedLifecycleOperationalHealthOptions>>().Value,
    timeProvider: services.GetRequiredService<TimeProvider>()));
builder.Services.Configure<ElsaInstanceLifecycleWorkerOptions>(builder.Configuration.GetSection(ElsaInstanceLifecycleWorkerOptions.ConfigurationSection));
builder.Services.AddScoped<ICatalogStore, EfCoreCatalogStore>();
builder.Services.AddScoped<IGovernedReleaseCatalogStore, GovernedReleaseCatalogStore>();
ReleaseManifestVerifierComposition.AddVerifier(builder.Services, builder.Configuration);
builder.Services.AddScoped<ReleaseManifestAdmissionService>();
builder.Services.AddScoped<GovernedReleaseCatalogIngestionService>();
builder.Services.AddScoped<IReleaseCatalogIngestionService>(services =>
    services.GetRequiredService<GovernedReleaseCatalogIngestionService>());
builder.Services.AddScoped<IPublicCatalogQueries, PublicCatalogQueries>();
builder.Services.AddScoped<PublicCatalogQueryService>();
builder.Services.AddScoped<IPublicSourceQueries, PublicSourceQueries>();
builder.Services.AddScoped<PublicSourceQueryService>();
builder.Services.AddScoped<IAccountWorkspaceStore, AccountWorkspaceStore>();
builder.Services.AddScoped<IWorkspaceOwnerProvisioner, WorkspacePermissionOwnerProvisioner>();
builder.Services.AddScoped<AccountWorkspaceService>();
builder.Services.AddScoped<WorkspaceSourceService>();
builder.Services.AddScoped<RuntimeConfigurationService>();
builder.Services.AddScoped<ControlIdentityReader>();
builder.Services.AddScoped<CustomerSessionIdentityReader>();
builder.Services.AddScoped<TrustedHeaderWorkspaceIdentityReader>();
builder.Services.AddScoped<WorkspaceAccessResolver>();
builder.Services.AddHostedService<ControlIdentityConfigurationValidator>();
builder.Services.AddScoped<CompositeWorkspaceIdentityReader>(services => new CompositeWorkspaceIdentityReader([
    services.GetRequiredService<ControlIdentityReader>(),
    services.GetRequiredService<CustomerSessionIdentityReader>(),
    services.GetRequiredService<TrustedHeaderWorkspaceIdentityReader>()
]));
builder.Services.AddScoped<IWorkspaceIdentityReader>(services =>
    services.GetRequiredService<CompositeWorkspaceIdentityReader>());
builder.Services.AddScoped<IAuthenticatedControlSessionReader>(services =>
    services.GetRequiredService<CompositeWorkspaceIdentityReader>());
builder.Services.AddSingleton<PublicCatalogCache>();
builder.Services.AddSingleton<IPublicCatalogCacheInvalidator>(services => services.GetRequiredService<PublicCatalogCache>());
builder.Services.AddScoped<IPackageSourceStore, PackageSourceStore>();
builder.Services.AddScoped<PackageSourceService>();
builder.Services.AddScoped<ISyncCatalogStore, SyncCatalogStore>();
builder.Services.AddScoped<ISyncRunStore, SyncRunStore>();
builder.Services.AddScoped<IApprovalStore, ApprovalStore>();
builder.Services.AddScoped<ICompatibilityQueries, CompatibilityQueries>();
builder.Services.AddScoped<IRuntimeConfigurationStore, RuntimeConfigurationStore>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<CompatibilityCheckService>();
builder.Services.AddScoped<IPackageCompatibilityService>(services => services.GetRequiredService<CompatibilityCheckService>());
builder.Services.AddScoped<DeploymentCockpitService>();
builder.Services.AddScoped<IWorkspaceDeploymentStore, DeploymentWorkspaceStore>();
builder.Services.AddScoped<IWorkspaceDeploymentTierStore, DeploymentWorkspaceStore>();
builder.Services.AddScoped<IWorkspaceArtifactStore, DeploymentWorkspaceStore>();
builder.Services.AddScoped<IWorkspaceArtifactUploadStore, DeploymentWorkspaceStore>();
builder.Services.AddScoped<IWorkspacePermissionStore, DeploymentWorkspaceStore>();
builder.Services.AddScoped<IWorkspaceDeploymentMutationStore, DeploymentWorkspaceStore>();
builder.Services.AddScoped<IWorkspaceDeploymentCommandStore, DeploymentWorkspaceStore>();
builder.Services.AddScoped<WorkspaceDeploymentService>();
builder.Services.AddScoped<AzureProviderOperationStore>();
builder.Services.AddScoped<IAzureProviderOperationStore>(services =>
    services.GetRequiredService<AzureProviderOperationStore>());
builder.Services.AddScoped<IAzureProviderResourceAssignmentStore>(services =>
    services.GetRequiredService<AzureProviderOperationStore>());
builder.Services.AddScoped<IAzureProviderRecoveryObservationStore>(services =>
    services.GetRequiredService<AzureProviderOperationStore>());
builder.Services.AddScoped<AzureProviderExecutor>();
builder.Services.AddScoped<IAzureProviderOperationService, AzureProviderOperationService>();
builder.Services.AddScoped<AzureProviderOperationWorker>();
builder.Services.AddScoped<IAzureProviderPlanSource, PersistedAzureProviderPlanSource>();
builder.Services.Configure<AzureProviderOperationOptions>(builder.Configuration.GetSection(AzureProviderOperationOptions.ConfigurationSection));
builder.Services.AddHostedService<ManagedAzureProviderConfigurationValidator>();
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("ElsaControl.Api");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}
else if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "data-protection-keys")));
}
else
{
    throw new InvalidOperationException("DataProtection:KeysPath must be configured to a stable, shared key-ring location before local engine credentials can be protected in production.");
}
builder.Services.AddScoped<DeploymentTierService>();
builder.Services.AddSingleton<IArtifactTypeRegistry, ArtifactTypeRegistry>();
builder.Services.AddSingleton<ArtifactEnvelopeValidator>();
builder.Services.AddScoped<IDeploymentArtifactReader, DeploymentArtifactReader>();
builder.Services.AddScoped<IDeploymentArtifactBuilder, DeploymentArtifactBuilder>();
builder.Services.AddScoped<WorkspaceArtifactService>();
builder.Services.Configure<ArtifactUploadOptions>(builder.Configuration.GetSection("ArtifactUploads"));
builder.Services.PostConfigure<ArtifactUploadOptions>(options =>
{
    var section = builder.Configuration.GetSection("ArtifactUploads");
    if (!section.GetSection(nameof(ArtifactUploadOptions.SampleGenerationEnabled)).Exists())
        options.SampleGenerationEnabled = builder.Environment.IsDevelopment();
});
builder.Services.AddScoped(services => new WorkspaceArtifactUploadService(
    services.GetRequiredService<IWorkspaceArtifactUploadStore>(),
    services.GetRequiredService<IWorkspaceArtifactStore>(),
    services.GetRequiredService<WorkspaceArtifactService>(),
    services.GetRequiredService<IDeploymentArtifactReader>(),
    services.GetRequiredService<IDeploymentArtifactBuilder>(),
    services.GetRequiredService<IOptions<ArtifactUploadOptions>>().Value,
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<WorkspacePermissionService>();
builder.Services.AddScoped<DeploymentValidationService>();
builder.Services.AddScoped<DeploymentDeployabilityService>();
builder.Services.AddScoped<DeploymentPromotionService>();
builder.Services.AddHttpClient<IEngineHealthProbe, HttpEngineHealthProbe>(client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddScoped<EngineHealthService>();
builder.Services.Configure<EngineVerificationOptions>(builder.Configuration.GetSection("Deployment:EngineVerification"));
builder.Services.AddScoped<DeploymentRunService>();
builder.Services.AddScoped<DeploymentCommandService>();
builder.Services.Configure<DeploymentWebhookDispatchOptions>(builder.Configuration.GetSection("Deployment:WebhookDispatch"));
builder.Services.AddHttpClient<IDeploymentWebhookSender, HttpDeploymentWebhookSender>(client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddScoped<DeploymentWebhookDispatchService>(services =>
    new DeploymentWebhookDispatchService(
        services.GetRequiredService<IWorkspaceDeploymentCommandStore>(),
        services.GetRequiredService<IDeploymentWebhookSender>(),
        services.GetRequiredService<IOptions<DeploymentWebhookDispatchOptions>>().Value,
        services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<DeploymentQueueWorker>();
builder.Services.AddScoped<RuntimeControlService>();
builder.Services.AddScoped<ConfirmationService>();
builder.Services.AddScoped<ObservabilityDriftService>();
builder.Services.Configure<WeaverOptions>(builder.Configuration.GetSection("Weaver"));
builder.Services.AddSingleton<WeaverRedactionService>();
builder.Services.AddScoped<WeaverWorkspaceTools>();
builder.Services.AddScoped<IWeaverSessionStore, WeaverSessionStore>();
builder.Services.AddScoped<FakeWeaverRuntime>();
builder.Services.AddScoped<CopilotWeaverRuntime>();
builder.Services.AddScoped<IWeaverRuntime>(services =>
{
    var weaverOptions = services.GetRequiredService<IOptions<WeaverOptions>>().Value;
    return weaverOptions.ProviderMode == WeaverProviderMode.Fake
        ? services.GetRequiredService<FakeWeaverRuntime>()
        : services.GetRequiredService<CopilotWeaverRuntime>();
});
builder.Services.AddScoped<WeaverSessionService>();
builder.Services.AddScoped<WeaverPlanService>();
builder.Services.AddScoped<WeaverPlanExecutionService>();
builder.Services.AddHostedService<WeaverConfigurationHostedService>();
var deploymentQueueWorkerEnabled = builder.Configuration.GetValue("Deployment:QueueWorker:Enabled", false);
if (deploymentQueueWorkerEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<DeploymentQueueHostedService>();
var instanceLifecycleWorkerEnabled = builder.Configuration.GetValue(ElsaInstanceLifecycleWorkerOptions.ConfigurationSection + ":Enabled", false);
var billingLifecycleWorkerEnabled = builder.Configuration.GetValue(OrganizationBillingLifecycleWorkerOptions.ConfigurationSection + ":Enabled", false);
if (billingLifecycleWorkerEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<OrganizationBillingLifecycleHostedService>();
if (instanceLifecycleWorkerEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<ElsaInstanceLifecycleHostedService>();
if (instanceLifecycleWorkerEnabled && azureInstanceLifecycleEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<ElsaInstanceProviderReconciliationHostedService>();
if (instanceLifecycleWorkerEnabled && azureInstanceLifecycleEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<ElsaInstanceDeletionHostedService>();
var azureProviderWorkerEnabled = builder.Configuration.GetValue("Deployment:AzureProvider:WorkerEnabled", false);
if (azureProviderWorkerEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<AzureProviderOperationHostedService>();
var webhookDispatchEnabled = builder.Configuration.GetValue("Deployment:WebhookDispatch:Enabled", false);
if (webhookDispatchEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<DeploymentWebhookDispatchHostedService>();
var engineVerificationEnabled = builder.Configuration.GetValue("Deployment:EngineVerification:Enabled", true);
if (engineVerificationEnabled && !builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<EngineVerificationHostedService>();
builder.Services.AddScoped<IPackageVersionDiscoveryClient, NuGetPackageSourceClient>();
builder.Services.AddScoped<IPackageArchiveDownloader, NuGetSyncPackageDownloader>();
builder.Services.AddScoped<IPackageArchiveManifestReader, PackageArchiveManifestReader>();
builder.Services.AddScoped<ManifestIngestionService>();
builder.Services.AddScoped<PackageSyncService>();
builder.Services.AddScoped<SyncRunCleanupService>();
builder.Services.AddSingleton<PackageSourceValidator>();
builder.Services.AddSingleton<PackageSourcePatternMatcher>();
builder.Services.AddSingleton<ManifestValidator>();
builder.Services.AddSingleton<ApprovalPolicy>();
builder.Services.AddSingleton<VersionRangeEvaluator>();
builder.Services.AddSingleton<InfrastructureProviderCatalog>();
builder.Services.AddOptions<RuntimeBuilderOptions>()
    .Bind(builder.Configuration.GetSection(RuntimeBuilderOptions.SectionName));
builder.Services.AddSingleton<RuntimeImageCatalog>();
builder.Services.AddSingleton<RuntimeImageValidator>();
builder.Services.AddSingleton<BundleFindingPolicy>();
builder.Services.AddSingleton<BundleFilePolicy>();
builder.Services.AddScoped<ElsaControl.RuntimeBuilder.Core.Builder.Renderers.IBundleFileRenderer, ElsaControl.RuntimeBuilder.Core.Builder.Renderers.AppSettingsBundleRenderer>();
builder.Services.AddScoped<ElsaControl.RuntimeBuilder.Core.Builder.Renderers.IBundleFileRenderer, ElsaControl.RuntimeBuilder.Core.Builder.Renderers.PackageLockBundleRenderer>();
builder.Services.AddScoped<ElsaControl.RuntimeBuilder.Core.Builder.Renderers.IBundleFileRenderer, ElsaControl.RuntimeBuilder.Core.Builder.Renderers.EnvExampleBundleRenderer>();
builder.Services.AddScoped<ElsaControl.RuntimeBuilder.Core.Builder.Renderers.IBundleFileRenderer, ElsaControl.RuntimeBuilder.Core.Builder.Renderers.ReadmeBundleRenderer>();
builder.Services.AddScoped<ElsaControl.RuntimeBuilder.Core.Builder.Renderers.IBundleFileRenderer, ElsaControl.RuntimeBuilder.Core.Builder.Renderers.ProgramReferenceBundleRenderer>();
builder.Services.AddScoped<IDeploymentTemplateRenderer, DockerComposeBundleRenderer>();
builder.Services.AddScoped<IDeploymentTemplateRenderer, AzureContainerAppsTemplateRenderer>();
builder.Services.AddScoped<IDeploymentTemplateRenderer, KubernetesHelmTemplateRenderer>();
builder.Services.AddScoped<DeploymentTemplateRegistry>();
builder.Services.AddScoped<BundleGenerationService>();
builder.Services.AddScoped<BuilderPlannerService>();
builder.Services.AddSingleton<SyncConcurrencyGuard>();
builder.Services.AddSingleton<SourceSyncActivityTracker>();
builder.Services.AddSingleton<SyncRunCancellationRegistry>();
builder.Services.AddSingleton<ManualSyncQueue>();
builder.Services.AddSingleton<PublicCatalogVisibilityPolicy>();
builder.Services.AddSingleton<PackageVersionPolicy>();
builder.Services.AddSingleton<ISyncDiagnostics, NoopSyncDiagnostics>();
// Skipped in Testing for the same reason the catalog migration below is: the schema is created per test, after the
// host has already started, so it does not exist yet when hosted services' StartAsync runs.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<SyncRunReconciliationHostedService>();
builder.Services.AddHostedService<ManualSyncHostedService>();
builder.Services.AddHostedService<ScheduledSyncHostedService>();

var app = builder.Build();
var adminConsoleAssetsExist = AdminConsoleAssetsExist(app.Environment);
var adminConsoleDevelopmentUrl = adminConsoleAssetsExist
    ? null
    : GetAdminConsoleDevelopmentUrl(app.Configuration);

var runtimeImageFindings = app.Services.GetRequiredService<RuntimeImageValidator>()
    .Validate(app.Services.GetRequiredService<RuntimeImageCatalog>().ListImages());
if (runtimeImageFindings.Count > 0)
    throw new InvalidOperationException($"Runtime image catalog is invalid: {string.Join(" ", runtimeImageFindings.Select(x => $"{x.Code} {x.Scope}"))}");

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var combinedPath = context.Request.PathBase.Add(context.Request.Path);
    if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
        (combinedPath.Equals("/admin") || combinedPath.Equals("/admin/")))
    {
        if (adminConsoleAssetsExist)
        {
            await Results.File(
                    Path.Combine(app.Environment.WebRootPath!, "admin", "index.html"),
                    "text/html")
                .ExecuteAsync(context);
            return;
        }

        if (adminConsoleDevelopmentUrl is not null)
        {
            await ProxyAdminConsoleDevelopmentServerAsync(
                context,
                context.RequestServices.GetRequiredService<IHttpClientFactory>(),
                adminConsoleDevelopmentUrl);
            return;
        }

        await Results.Content(AdminConsoleFallbackPage(), "text/html").ExecuteAsync(context);
        return;
    }

    await next(context);
});
app.UseAdminDashboardRequestForgeryGuard();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthorization();

app.MapOpenApi();
app.MapGet("/health", (IConfiguration configuration) =>
{
    var buildNumber = SafeHealthIdentifier(configuration["Application:BuildNumber"]);
    var imageId = SafeHealthIdentifier(configuration["ELSA_CONTROL_IMAGE_ID"]);
    return Results.Ok(new { status = "ok", buildNumber, imageId });
});
app.MapGet("/", () => "Elsa Control API");
if (adminConsoleDevelopmentUrl is not null)
{
    app.MapMethods("/admin/{*path}", [HttpMethods.Get, HttpMethods.Head], (
        HttpContext context,
        IHttpClientFactory httpClientFactory) =>
        ProxyAdminConsoleDevelopmentServerAsync(context, httpClientFactory, adminConsoleDevelopmentUrl));
}
else if (!adminConsoleAssetsExist)
{
    app.MapGet("/admin/", () => Results.Content(AdminConsoleFallbackPage(), "text/html"));
    app.MapGet("/admin/{*path:nonfile}", () => Results.Content(AdminConsoleFallbackPage(), "text/html"));
}
app.MapCustomerAuthEndpoints();
app.MapManagedElsaHandoffEndpoints();
app.MapAdminDashboardAuthEndpoints();
app.MapPublicPackageEndpoints();
app.MapPublicSourceEndpoints();
app.MapPublicFeatureEndpoints();
app.MapBuilderEndpoints();
app.MapCompatibilityEndpoints();
app.MapWorkspaceMeEndpoints();
app.MapOrganizationWorkspaceEndpoints();
app.MapOrganizationBillingEndpoints();
app.MapWorkspaceSourceEndpoints();
app.MapWorkspacePackageEndpoints();
app.MapWorkspaceReleaseCatalogEndpoints();
app.MapWorkspaceBuilderEndpoints();
app.MapWorkspaceRuntimeConfigurationEndpoints();
app.MapWorkspaceDeploymentEndpoints();
app.MapManagedElsaInstanceEndpoints();
app.MapWorkspacePermissionManagementEndpoints();
app.MapWorkspaceArtifactEndpoints();
app.MapWorkspaceWeaverEndpoints();
app.MapRuntimeCommandEndpoints();
app.MapAdminApplicationEndpoints();
app.MapAdminSourceEndpoints();
app.MapAdminSyncEndpoints();
app.MapAdminPackageEndpoints();
app.MapAdminApprovalEndpoints();
app.MapAdminValidationEndpoints();
app.MapAdminWorkspaceEntitlementEndpoints();
app.MapAdminOrganizationInternalEntitlementEndpoints();
app.MapAdminReleaseCatalogEndpoints();
app.MapConsoleLogStreaming();
if (adminConsoleAssetsExist)
    app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");

app.Run();

static string SafeHealthIdentifier(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        return "unknown";
    if (!char.IsAsciiLetterOrDigit(value[0]))
        return "unknown";

    foreach (var character in value)
    {
        if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '+' or '-'))
            return "unknown";
    }

    return value;
}

static bool AdminConsoleAssetsExist(IWebHostEnvironment environment) =>
    !string.IsNullOrWhiteSpace(environment.WebRootPath) &&
    File.Exists(Path.Combine(environment.WebRootPath, "admin", "index.html"));

static Uri? GetAdminConsoleDevelopmentUrl(IConfiguration configuration)
{
    var value = configuration[AdminDashboardAuthenticationDefaults.DevelopmentUrlConfigurationKey];
    return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        ? uri
        : null;
}

static async Task ProxyAdminConsoleDevelopmentServerAsync(
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    Uri adminConsoleDevelopmentUrl)
{
    var targetUri = GetAdminConsoleDevelopmentProxyUri(adminConsoleDevelopmentUrl, context.Request.Path, context.Request.QueryString);
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
    CopyProxyRequestHeaders(context.Request, request);

    var client = httpClientFactory.CreateClient(AdminDashboardAuthenticationDefaults.DevelopmentUrlConfigurationKey);
    using var response = await SendAdminConsoleDevelopmentProxyRequestAsync(
        client,
        request,
        context,
        adminConsoleDevelopmentUrl);
    if (response is null)
        return;

    context.Response.StatusCode = (int)response.StatusCode;
    CopyProxyResponseHeaders(response, context.Response);

    if (!HttpMethods.IsHead(context.Request.Method))
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

static async Task<HttpResponseMessage?> SendAdminConsoleDevelopmentProxyRequestAsync(
    HttpClient client,
    HttpRequestMessage request,
    HttpContext context,
    Uri adminConsoleDevelopmentUrl)
{
    try
    {
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }
    catch (HttpRequestException)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/html";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.WriteAsync(AdminConsoleDevelopmentUnavailablePage(adminConsoleDevelopmentUrl), context.RequestAborted);
        return null;
    }
}

static Uri GetAdminConsoleDevelopmentProxyUri(Uri adminConsoleDevelopmentUrl, PathString requestPath, QueryString queryString)
{
    var basePath = adminConsoleDevelopmentUrl.AbsolutePath.TrimEnd('/');
    var path = requestPath.Value ?? "/admin/";
    var builder = new UriBuilder(adminConsoleDevelopmentUrl)
    {
        Path = $"{basePath}{path}",
        Query = queryString.HasValue ? queryString.Value![1..] : string.Empty
    };
    return builder.Uri;
}

static void CopyProxyRequestHeaders(HttpRequest source, HttpRequestMessage target)
{
    foreach (var header in source.Headers)
    {
        if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!target.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            target.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }
}

static void CopyProxyResponseHeaders(HttpResponseMessage source, HttpResponse target)
{
    foreach (var header in source.Headers)
        target.Headers[header.Key] = header.Value.ToArray();

    foreach (var header in source.Content.Headers)
        target.Headers[header.Key] = header.Value.ToArray();

    target.Headers.Remove("transfer-encoding");
}

static string AdminConsoleFallbackPage() =>
    """
    <!doctype html>
    <html lang="en">
      <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Elsa Control Console</title>
        <style>
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #f8fafc; color: #0f172a; }
          main { width: min(440px, calc(100vw - 32px)); border: 1px solid #e2e8f0; border-radius: 8px; background: #fff; padding: 24px; box-shadow: 0 16px 40px rgb(15 23 42 / 0.08); }
          h1 { margin: 0 0 8px; font-size: 1.35rem; }
          p { margin: 0 0 20px; color: #475569; line-height: 1.5; }
          a { display: inline-flex; min-height: 40px; align-items: center; justify-content: center; border-radius: 6px; background: #2563eb; color: #fff; padding: 0 16px; font-weight: 600; text-decoration: none; }
        </style>
      </head>
      <body>
        <main>
          <h1>Elsa Control Console</h1>
          <p>Sign in with the configured local identity provider to continue.</p>
          <a href="/api/auth/login?returnUrl=%2Fadmin%2Foverview">Sign in</a>
        </main>
      </body>
    </html>
    """;

static string AdminConsoleDevelopmentUnavailablePage(Uri adminConsoleDevelopmentUrl) =>
    $$"""
    <!doctype html>
    <html lang="en">
      <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Elsa Control Console</title>
        <style>
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #f8fafc; color: #0f172a; }
          main { width: min(520px, calc(100vw - 32px)); border: 1px solid #e2e8f0; border-radius: 8px; background: #fff; padding: 24px; box-shadow: 0 16px 40px rgb(15 23 42 / 0.08); }
          h1 { margin: 0 0 8px; font-size: 1.35rem; }
          p { margin: 0 0 20px; color: #475569; line-height: 1.5; }
          code { border-radius: 4px; background: #f1f5f9; padding: 2px 5px; }
        </style>
      </head>
      <body>
        <main>
          <h1>Elsa Control Console</h1>
          <p>The local console dev server is not responding at <code>{{adminConsoleDevelopmentUrl}}</code>.</p>
          <p>Start the Aspire console resource or run <code>npm run dev</code> in <code>src/ElsaControl.Console</code>, then refresh this page.</p>
        </main>
      </body>
    </html>
    """;

[UsedImplicitly]
public partial class Program;
