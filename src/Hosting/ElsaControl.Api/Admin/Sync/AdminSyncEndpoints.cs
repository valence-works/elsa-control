using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using System.Security.Claims;

namespace ElsaControl.Api.Admin.Sync;

public static class AdminSyncEndpoints
{
    public static IEndpointRouteBuilder MapAdminSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Sync");

        group.MapPost("/sync", async (PackageSyncService sync, ManualSyncQueue queue, ISyncRunStore syncRuns, CancellationToken cancellationToken) =>
        {
            var start = await sync.StartManualAllAsync(cancellationToken);
            if (start.WorkItem is not null)
                queue.Enqueue(start.WorkItem);

            return Results.Ok(await ToResponseAsync(start.Run, syncRuns, cancellationToken));
        });

        group.MapPost("/sync/sources/{sourceId:guid}", async (Guid sourceId, PackageSyncService sync, ManualSyncQueue queue, ISyncRunStore syncRuns, CancellationToken cancellationToken) =>
        {
            var start = await sync.StartManualSourceAsync(sourceId, cancellationToken);
            if (start.WorkItem is not null)
                queue.Enqueue(start.WorkItem);

            return Results.Ok(await ToResponseAsync(start.Run, syncRuns, cancellationToken, start.Source));
        });

        group.MapPost("/sync/packages/{packageId}", (string packageId) =>
            Results.BadRequest(new { error = "Manual package sync is not available until package source ownership is known." }));

        group.MapGet("/sync-runs", async (ISyncRunStore syncRuns, CancellationToken cancellationToken) =>
        {
            var runs = await syncRuns.ListAsync(cancellationToken);
            var metadata = await syncRuns.GetListMetadataAsync(runs.Select(x => x.Id).ToList(), cancellationToken);
            return Results.Ok(runs.Select(run => ToResponse(run, metadata.GetValueOrDefault(run.Id))));
        });

        group.MapGet("/sync-runs/deletion-preview", async (DateTimeOffset completedBefore, SyncRunCleanupService cleanup, CancellationToken cancellationToken) =>
        {
            var result = await cleanup.PreviewDeleteBeforeAsync(completedBefore, cancellationToken);
            if (!result.IsValid)
                return Results.BadRequest(new { error = "completedBefore must not be later than the current server time." });

            return Results.Ok(ToResponse(result.Preview!));
        });

        group.MapDelete("/sync-runs", async (DateTimeOffset completedBefore, SyncRunCleanupService cleanup, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var result = await cleanup.DeleteBeforeAsync(completedBefore, Actor(httpContext), cancellationToken);
            if (!result.IsValid)
                return Results.BadRequest(new { error = "completedBefore must not be later than the current server time." });

            return Results.Ok(ToResponse(result.Cleanup!));
        });

        group.MapGet("/sync-runs/{id:guid}", async (Guid id, ISyncRunStore syncRuns, CancellationToken cancellationToken) =>
        {
            var run = await syncRuns.GetAsync(id, cancellationToken);
            if (run is null)
                return Results.NotFound();

            return Results.Ok(await ToResponseAsync(run, syncRuns, cancellationToken));
        });

        group.MapPost("/sync-runs/{id:guid}/cancel", async (Guid id, ISyncRunStore syncRuns, SyncRunCancellationRegistry cancellationRegistry, CancellationToken cancellationToken) =>
        {
            var run = await syncRuns.GetAsync(id, cancellationToken);
            if (run is null)
                return Results.NotFound();

            if (run.Status != SyncRunStatus.Running)
                return Results.Conflict(new { error = "Sync run is not running." });

            if (!cancellationRegistry.Cancel(id))
                return Results.Conflict(new { error = "Sync run is not active on this API host." });

            return Results.Ok(await ToResponseAsync(run, syncRuns, cancellationToken));
        });

        group.MapDelete("/sync-runs/{id:guid}", async (Guid id, SyncRunCleanupService cleanup, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var result = await cleanup.DeleteAsync(id, Actor(httpContext), cancellationToken);
            if (result.IsConflict)
                return Results.Conflict(new { error = $"Sync run '{id}' is {result.NonTerminalStatus} and cannot be deleted." });

            return Results.Ok(ToResponse(result.Cleanup!));
        });

        return endpoints;
    }

    private static async Task<AdminSyncRunResponse> ToResponseAsync(SyncRun run, ISyncRunStore syncRuns, CancellationToken cancellationToken, SyncRunSourceReference? source = null)
    {
        var metadata = await syncRuns.GetListMetadataAsync([run.Id], cancellationToken);
        return ToResponse(run, AddSource(metadata.GetValueOrDefault(run.Id), source));
    }

    private static AdminSyncRunResponse ToResponse(SyncRun run, SyncRunListMetadata? metadata = null)
    {
        var sources = metadata?.Sources ?? SourceReferencesFromItems(run.Items);
        var itemCount = metadata?.ItemCount ?? run.Items.Count;
        return new AdminSyncRunResponse(
            run.Id,
            run.Trigger,
            run.Mode,
            run.Status,
            run.StartedAt,
            run.CompletedAt,
            run.Error,
            run.SummaryCountersJson,
            itemCount,
            sources.Select(ToResponse).ToList(),
            run.Items.Select(ToResponse).ToList());
    }

    private static AdminSyncRunItemResponse ToResponse(SyncRunItem item) =>
        new(
            item.Id,
            item.SourceId,
            item.PackageId,
            item.Version,
            item.Status,
            item.Message,
            item.Error,
            item.StartedAt,
            item.CompletedAt);

    private static AdminSyncRunCleanupPreviewResponse ToResponse(SyncRunCleanupPreview preview) =>
        new(
            preview.CompletedBefore,
            preview.EligibleRunCount,
            preview.EligibleItemCount,
            preview.ExcludedRunCount,
            preview.OldestEligibleCompletedAt,
            preview.NewestEligibleCompletedAt);

    private static AdminSyncRunCleanupResultResponse ToResponse(SyncRunCleanupResult result) =>
        new(
            result.DeletedRunCount,
            result.DeletedItemCount,
            result.ExcludedRunCount,
            result.NotFoundRunCount,
            result.CompletedBefore,
            result.DeletedRunIds);

    private static AdminSyncRunSourceResponse ToResponse(SyncRunSourceReference source) =>
        new(source.Id, source.Name);

    private static string? Actor(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.User.Identity?.Name;

    private static SyncRunListMetadata? AddSource(SyncRunListMetadata? metadata, SyncRunSourceReference? source)
    {
        if (source is null || metadata?.Sources.Any(x => x.Id == source.Id) == true)
            return metadata;

        var sources = metadata?.Sources.ToList() ?? [];
        sources.Add(source);
        return new SyncRunListMetadata(metadata?.ItemCount ?? 0, sources);
    }

    private static IReadOnlyList<SyncRunSourceReference> SourceReferencesFromItems(IReadOnlyList<SyncRunItem> items) =>
        items
            .Where(item => item.SourceId.HasValue)
            .Select(item => item.SourceId!.Value)
            .Distinct()
            .Select(sourceId => new SyncRunSourceReference(sourceId, null))
            .ToList();
}
