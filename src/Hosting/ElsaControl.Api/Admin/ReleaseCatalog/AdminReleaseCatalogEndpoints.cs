using ElsaControl.Api.Authentication;
using ElsaControl.Api.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.ReleaseCatalog;

public static class AdminReleaseCatalogEndpoints
{
    public static IEndpointRouteBuilder MapAdminReleaseCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/release-catalog")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Release Catalog");

        group.MapPost("/manifests", async (
            AdminReleaseManifestIngestionRequest? request,
            IReleaseCatalogIngestionService ingestion,
            IOptions<ReleaseCatalogAdmissionOptions> configuredOptions,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
                return Problem(
                    httpContext,
                    "Invalid release-manifest request.",
                    "releaseCatalog.request.invalid",
                    StatusCodes.Status400BadRequest,
                    "A release-manifest request body is required.");

            var options = configuredOptions.Value;
            var result = await ingestion.AdmitAsync(
                new ReleaseManifestArtifact(
                    request.Reference ?? "",
                    request.Digest ?? "",
                    request.Payload ?? ""),
                options.ToAdmissionOptions(),
                cancellationToken);

            if (!result.Accepted)
                return Rejected(httpContext, result, "The release manifest could not be admitted into the governed catalog.");

            var response = new AdminReleaseCatalogAdmissionResponse(
                result.WriteStatus ?? GovernedReleaseCatalogWriteStatus.Stored,
                result.Entries.Select(ReleaseCatalogApiMappings.ToResponse).ToArray());

            return result.WriteStatus == GovernedReleaseCatalogWriteStatus.Stored
                ? Results.Created("/api/admin/release-catalog/manifests", response)
                : Results.Ok(response);
        }).WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024));

        group.MapPost("/manifests/promote-supported", async (
            AdminReleaseCatalogPromoteRequest? request,
            IReleaseCatalogIngestionService ingestion,
            IOptions<ReleaseCatalogAdmissionOptions> configuredOptions,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ManifestDigest))
                return Problem(
                    httpContext,
                    "Invalid promotion request.",
                    "releaseCatalog.request.invalid",
                    StatusCodes.Status400BadRequest,
                    "A release-manifest digest is required.");

            var result = await ingestion.PromoteToSupportedAsync(
                request.ManifestDigest,
                configuredOptions.Value.ToAdmissionOptions(),
                cancellationToken);

            if (!result.Accepted)
                return Rejected(
                    httpContext,
                    result,
                    "The admitted release could not be promoted to supported.",
                    promotion: true);

            return Results.Ok(new AdminReleaseCatalogAdmissionResponse(
                result.WriteStatus ?? GovernedReleaseCatalogWriteStatus.Stored,
                result.Entries.Select(ReleaseCatalogApiMappings.ToResponse).ToArray()));
        });

        return endpoints;
    }

    private static IResult Rejected(
        HttpContext httpContext,
        GovernedReleaseCatalogAdmissionResult result,
        string detail,
        bool promotion = false)
    {
        var feedUnavailable = result.Findings.Any(finding =>
            finding.Code == GovernedReleasePackageResolution.FeedUnavailableCode);
        var status = result.WriteStatus == GovernedReleaseCatalogWriteStatus.Conflict
            ? StatusCodes.Status409Conflict
            : feedUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status422UnprocessableEntity;
        var code = result.WriteStatus == GovernedReleaseCatalogWriteStatus.Conflict
            ? "releaseCatalog.identity.conflict"
            : feedUnavailable
                ? GovernedReleasePackageResolution.FeedUnavailableCode
                : promotion
                    ? "releaseCatalog.promotion.rejected"
                    : "releaseCatalog.admission.rejected";
        var title = result.WriteStatus == GovernedReleaseCatalogWriteStatus.Conflict
            ? "Release catalog identity conflict."
            : feedUnavailable
                ? "Package feed is unavailable."
                : promotion
                    ? "Release was not promoted to supported."
                    : "Release manifest was not admitted.";
        return Problem(
            httpContext,
            title,
            code,
            status,
            detail,
            result.Findings,
            result.WriteStatus == GovernedReleaseCatalogWriteStatus.Conflict
                ? result.Entries.Select(ReleaseCatalogApiMappings.ToResponse).ToArray()
                : null,
            result.WriteStatus == GovernedReleaseCatalogWriteStatus.Conflict
                ? result.IncomingEntries?.Select(ReleaseCatalogApiMappings.ToResponse).ToArray()
                : null,
            retryable: feedUnavailable);
    }

    private static IResult Problem(
        HttpContext httpContext,
        string title,
        string code,
        int statusCode,
        string detail,
        IReadOnlyList<GovernedReleaseCatalogFinding>? findings = null,
        IReadOnlyList<ReleaseCatalogEntryResponse>? existing = null,
        IReadOnlyList<ReleaseCatalogEntryResponse>? incoming = null,
        bool retryable = false)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["traceId"] = httpContext.TraceIdentifier,
            ["findings"] = findings?.Select(x => new { x.Code, x.Scope, x.Message }).ToArray()
        };
        if (retryable)
            extensions["retryable"] = true;
        if (existing is not null)
            extensions["existing"] = existing;
        if (incoming is not null)
            extensions["incoming"] = incoming;
        return Results.Problem(
            type: $"urn:elsa-control:problem:{code.Replace(".", "-", StringComparison.Ordinal)}",
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: extensions);
    }
}
