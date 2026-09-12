using Azure.Provisioning.AppService;
using Azure.Provisioning.Sql;

var builder = DistributedApplication.CreateBuilder(args);
var adminApiKey = builder.AddParameter("adminApiKey", secret: true);
var applicationBuildNumber = Environment.GetEnvironmentVariable("APPLICATION_BUILD_NUMBER")
    ?? Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER");

builder.AddAzureAppServiceEnvironment("elsa-control")
    // Production observability is Application Insights/Log Analytics behind Entra RBAC (#266, #302); the
    // Aspire dashboard web app and its OTLP sidecar are not provisioned. The dashboard still runs locally.
    .WithDashboard(false)
    .ConfigureInfrastructure(infrastructure =>
    {
        var plan = infrastructure.GetProvisionableResources().OfType<AppServicePlan>().Single();
        // B2 (2 cores, 3.5 GB): the managed-instance provider runs the Azure CLI and Bicep inside the API
        // container; on B1 the API alone sits near 80% memory, leaving too little for a provisioning run (#314).
        plan.Sku = new AppServiceSkuDescription
        {
            Name = "B2",
            Tier = "Basic",
            Capacity = 1
        };
    });

var api = builder.AddProject<Projects.ElsaControl_Api>("api")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Authentication__ApiKey", adminApiKey);

if (!string.IsNullOrWhiteSpace(applicationBuildNumber))
{
    api.WithEnvironment("Application__BuildNumber", applicationBuildNumber);
}

if (builder.ExecutionContext.IsPublishMode)
{
    var sql = builder.AddAzureSqlServer("control-sql")
        .ConfigureInfrastructure(infrastructure =>
        {
            var sqlDatabase = infrastructure.GetProvisionableResources().OfType<SqlDatabase>().Single();
            sqlDatabase.Sku = new SqlSku
            {
                Name = "GP_S_Gen5",
                Tier = "GeneralPurpose",
                Family = "Gen5",
                Capacity = 1
            };
            sqlDatabase.MinCapacity = 0.5;
            sqlDatabase.AutoPauseDelay = 60;
            // Aspire defaults new Azure SQL databases onto the free-limit offer, which Azure refuses
            // for this service objective and region ("Provisioning of free limit database is not
            // supported"). Provision it as an ordinary serverless database instead.
            sqlDatabase.UseFreeLimit = false;
            sqlDatabase.FreeLimitExhaustionBehavior = null!;
            sqlDatabase.RequestedBackupStorageRedundancy = SqlBackupStorageRedundancy.Zone;
            sqlDatabase.IsZoneRedundant = false;
        });
    var database = sql.AddDatabase("Catalog");

    // Published deployments authenticate operators against Microsoft Entra ID. The local run path
    // below uses its own Keycloak container instead, so these settings only apply when publishing.
    var entraTenantId = builder.AddParameter("entraTenantId");
    var entraClientId = builder.AddParameter("entraClientId");
    var entraClientSecret = builder.AddParameter("entraClientSecret", secret: true);
    var builderClientApiKey = builder.AddParameter("builderClientApiKey", secret: true);
    // Entra's v2.0 endpoint issues an `iss` equal to the authority; the v1 endpoint does not, which
    // would fail issuer validation.
    var entraAuthority = ReferenceExpression.Create($"https://login.microsoftonline.com/{entraTenantId}/v2.0");

    // Aspire defaults App Service sites to 30 workers, which a Basic plan rejects outright.
    api.PublishAsAzureAppServiceWebsite((_, site) => site.SiteConfig.NumberOfWorkers = 1);

    // Aspire's App Service integration injects AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential
    // itself; setting it here as well makes App Service reject the deployment with a duplicate
    // app-setting conflict.
    api.WithReference(database)
        .WithEnvironment("Database__Provider", "SqlServer")
        // The API refuses to start in Production without a stable data-protection key ring. On App
        // Service Linux, /home is the Azure Files-backed share that survives restarts and is shared
        // across instances, which is exactly what the key ring needs.
        .WithEnvironment("DataProtection__KeysPath", "/home/data-protection-keys")
        .WithEnvironment("WEBSITES_ENABLE_APP_SERVICE_STORAGE", "true")
        .WithEnvironment("Authentication__ControlIdentity__Provider", "MicrosoftEntra")
        .WithEnvironment("Authentication__ControlIdentity__Authority", entraAuthority)
        .WithEnvironment("Authentication__ControlIdentity__Issuer", entraAuthority)
        .WithEnvironment("Authentication__ControlIdentity__Audience", entraClientId)
        .WithEnvironment("Authentication__ControlIdentity__ClientId", entraClientId)
        .WithEnvironment("Authentication__ControlIdentity__ClientSecret", entraClientSecret)
        .WithEnvironment("Authentication__ControlIdentity__RedirectUri", "/api/auth/callback")
        .WithEnvironment("Authentication__ControlIdentity__PostLogoutRedirectUri", "/admin")
        .WithEnvironment("Authentication__ControlIdentity__RequireHttpsMetadata", "true")
        // Entra only issues `email` when the optional claim is configured and the account has one,
        // so fall back to preferred_username for both display name and email.
        .WithEnvironment("Authentication__ControlIdentity__Claims__DisplayName__0", "name")
        .WithEnvironment("Authentication__ControlIdentity__Claims__DisplayName__1", "preferred_username")
        .WithEnvironment("Authentication__ControlIdentity__Claims__Email__0", "email")
        .WithEnvironment("Authentication__ControlIdentity__Claims__Email__1", "preferred_username")
        // The admin dashboard accepts the operator's Entra session rather than a separate API key.
        .WithEnvironment("Authentication__Admin__AllowAuthenticatedCustomerSession", "true")
        // The Elsa Hub runtime-builder configurator calls the public builder API from the browser, so
        // its origins are allow-listed and it authenticates /api/builder/bundle with a client API key.
        .WithEnvironment("Authentication__BuilderClientApiKey", builderClientApiKey)
        .WithEnvironment("Cors__BuilderClientOrigins__0", "https://www.elsaworkflows.io")
        .WithEnvironment("Cors__BuilderClientOrigins__1", "https://elsaworkflows.io");
}
else
{
    const string keycloakAuthority = "https://127.0.0.1:8080/realms/elsa-control";
    const string keycloakClientId = "elsa-control-console";
    const string keycloakClientSecret = "local-dev-secret";
    var keycloakAdminUsername = builder.AddParameter("keycloak-admin-username", "admin");
    var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", "admin", secret: true);
    var keycloakRealmImport = Path.GetFullPath(
        Path.Combine(builder.AppHostDirectory, "../../../dev/keycloak/elsa-control-realm.json"));
    var keycloak = builder.AddKeycloak(
            "keycloak",
            port: 8080,
            adminUsername: keycloakAdminUsername,
            adminPassword: keycloakAdminPassword)
        .WithDataVolume("elsa-control-keycloak-data")
        .WithRealmImport(keycloakRealmImport);
    var console = builder.AddViteApp("console", "../ElsaControl.Console")
        .WithReference(api)
        .WithEnvironment("CATALOG_API_PROXY_TARGET", api.GetEndpoint("http"))
        .WaitFor(api);

    api.WithEnvironment("Database__Provider", "Sqlite")
        .WithEnvironment("Console__DevelopmentUrl", console.GetEndpoint("http"))
        .WithEnvironment("ConnectionStrings__Catalog", "Data Source=elsa-control-catalog-dev.db")
        .WithEnvironment("Authentication__ControlIdentity__Provider", "Keycloak")
        .WithEnvironment("Authentication__ControlIdentity__Authority", keycloakAuthority)
        .WithEnvironment("Authentication__ControlIdentity__Issuer", keycloakAuthority)
        .WithEnvironment("Authentication__ControlIdentity__Audience", keycloakClientId)
        .WithEnvironment("Authentication__ControlIdentity__ClientId", keycloakClientId)
        .WithEnvironment("Authentication__ControlIdentity__ClientSecret", keycloakClientSecret)
        .WithEnvironment("Authentication__ControlIdentity__RedirectUri", "/api/auth/callback")
        .WithEnvironment("Authentication__ControlIdentity__PostLogoutRedirectUri", "/admin")
        .WithEnvironment("Authentication__ControlIdentity__RequireHttpsMetadata", "false")
        .WithEnvironment("Authentication__Admin__AllowAuthenticatedCustomerSession", "true")
        .WithEnvironment("Authentication__WorkspaceTrustedHeaders__Enabled", "false")
        .WaitFor(keycloak);
}

builder.Build().Run();
