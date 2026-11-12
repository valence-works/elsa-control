using CShells.AspNetCore.Configuration;
using CShells.AspNetCore.Extensions;
using CShells.DependencyInjection;
using CShells.FastEndpoints.Features;
using Elsa.Common.ShellFeatures;
using Elsa.Expressions.JavaScript.ShellFeatures;
using Elsa.Http.ShellFeatures;
using Elsa.Identity.ShellFeatures;
using Elsa.Persistence.EFCore.Sqlite.ShellFeatures;
using Elsa.Persistence.EFCore.SqlServer.ShellFeatures;
using Elsa.Resilience.ShellFeatures;
using Elsa.SasTokens.ShellFeatures;
using Elsa.Scheduling.ShellFeatures;
using Elsa.ShellFeatures;
using Elsa.Tenants.AspNetCore.ShellFeatures;
using Elsa.Tenants.ShellFeatures;
using Elsa.Workflows.Api.ShellFeatures;
using Elsa.Workflows.Management.ShellFeatures;
using Elsa.Workflows.Runtime.Distributed.ShellFeatures;
using Elsa.Workflows.Runtime.ShellFeatures;
using Elsa.Workflows.ShellFeatures;
using ElsaControl.SharedEngine.Spike;

var builder = WebApplication.CreateBuilder(args);
var options = new SpikeOptions();
builder.Configuration.GetSection("Spike").Bind(options);
options.SqlConnection ??= Environment.GetEnvironmentVariable("SPIKE_SQL_CONNECTION");
if (string.IsNullOrWhiteSpace(options.DataDirectory))
    options.DataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(options.DataDirectory);

builder.AddShells(shells => shells
    .WithHostAssemblies()
    .WithAssemblies(
        typeof(Program).Assembly,
        typeof(ElsaFeature).Assembly,
        typeof(ElsaFastEndpointsFeature).Assembly,
        typeof(FastEndpointsFeature).Assembly,
        typeof(SystemClockFeature).Assembly,
        typeof(JavaScriptFeature).Assembly,
        typeof(ResilienceFeature).Assembly,
        typeof(SasTokensFeature).Assembly,
        typeof(WorkflowManagementFeature).Assembly,
        typeof(WorkflowInstancesFeature).Assembly,
        typeof(WorkflowRuntimeFeature).Assembly,
        typeof(WorkflowsFeature).Assembly,
        typeof(DistributedRuntimeFeature).Assembly,
        typeof(WorkflowsApiFeature).Assembly,
        typeof(IdentityFeature).Assembly,
        typeof(HttpFeature).Assembly,
        typeof(SchedulingFeature).Assembly,
        typeof(SqliteWorkflowPersistenceShellFeature).Assembly,
        typeof(SqlServerWorkflowPersistenceShellFeature).Assembly,
        typeof(TenantsFeature).Assembly,
        typeof(MultitenantHttpRoutingFeature).Assembly)
    .WithWebRouting(routing =>
    {
        routing.EnablePathRouting = false;
        routing.EnableHostRouting = true;
        routing.ExcludePaths = ["/health"];
    })
    .WithAuthenticationAndAuthorization()
    .AddSpikeShells(options));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<Microsoft.AspNetCore.Routing.MatcherPolicy, ShellHostEndpointSelectorPolicy>();
builder.Services.AddSingleton<CShells.Lifecycle.IShellLifecycleSubscriber, ShellEndpointHostMetadataSubscriber>();
builder.Services.AddHealthChecks();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseRouting();
app.MapHealthChecks("/health");
app.MapShells();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<SpikeHttpGateMiddleware>();
app.Run();

public partial class Program;
