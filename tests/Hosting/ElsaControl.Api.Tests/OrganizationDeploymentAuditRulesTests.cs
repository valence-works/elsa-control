using ElsaControl.Api.OrganizationDeployments;

namespace ElsaControl.Api.Tests;

public sealed class OrganizationDeploymentAuditRulesTests
{
    [Theory]
    [InlineData(null, null, 1, 50, true)]
    [InlineData(1, 50, 1, 50, true)]
    [InlineData(2, 100, 2, 100, true)]
    [InlineData(0, 50, 0, 50, false)]
    [InlineData(1, 0, 1, 0, false)]
    [InlineData(1, 101, 1, 101, false)]
    [InlineData(-1, 10, -1, 10, false)]
    public void Normalizes_or_rejects_page_arguments(
        int? page,
        int? pageSize,
        int expectedPage,
        int expectedPageSize,
        bool valid)
    {
        var accepted = OrganizationDeploymentAuditRules.TryNormalizePage(page, pageSize, out var normalizedPage, out var normalizedPageSize);

        Assert.Equal(valid, accepted);
        Assert.Equal(expectedPage, normalizedPage);
        Assert.Equal(expectedPageSize, normalizedPageSize);
    }

    [Theory]
    [InlineData("control-bff", true)]
    [InlineData("a", true)]
    [InlineData("fn_1.preview-2", true)]
    [InlineData("Control-Bff", false)]
    [InlineData("-leading", false)]
    [InlineData("has space", false)]
    [InlineData("ops@elsacloud.app", false)]
    [InlineData("sk_live_secret", false)]
    public void Function_name_follows_the_contract(string value, bool expected) =>
        Assert.Equal(expected, OrganizationDeploymentAuditRules.IsSafeFunctionName(value));

    [Theory]
    [InlineData("78bcf45", true)]
    [InlineData("78bcf459aa11bb22cc33dd44ee55ff6677889900", true)]
    [InlineData("ABCDEF0", false)]
    [InlineData("abc", false)]
    [InlineData("https://github.com/org/repo@78bcf45", false)]
    [InlineData("https://user:token@github.com/org/repo", false)]
    public void Source_revision_is_lowercase_hex_only(string value, bool expected) =>
        Assert.Equal(expected, OrganizationDeploymentAuditRules.IsSafeSourceRevision(value));

    [Fact]
    public void Approved_scope_accepts_up_to_eight_safe_labels()
    {
        Assert.True(OrganizationDeploymentAuditRules.IsSafeApprovedScope(
            ["function-only", "control-bff", "no-frontend-publish"]));
        Assert.False(OrganizationDeploymentAuditRules.IsSafeApprovedScope(
            ["function-only", "ops@elsacloud.app"]));
        Assert.False(OrganizationDeploymentAuditRules.IsSafeApprovedScope(
            Enumerable.Range(0, 9).Select(i => $"scope-{i}").ToArray()));
        Assert.False(OrganizationDeploymentAuditRules.IsSafeApprovedScope(["function-only", "function-only"]));
    }

    [Theory]
    [InlineData("stable-non-identifying-id", true)]
    [InlineData("audit:2026-09-20", true)]
    [InlineData("cus_hosted_owner", false)]
    [InlineData("sub_hosted_test", false)]
    [InlineData("user-123", false)]
    [InlineData("ada@example.test", false)]
    [InlineData("203.0.113.10", false)]
    public void Opaque_ids_reject_identifying_values(string value, bool expected) =>
        Assert.Equal(expected, OrganizationDeploymentAuditRules.IsSafeOpaqueId(value));

    [Fact]
    public void Projection_keeps_only_the_sanitized_contract_fields()
    {
        var occurredAt = DateTimeOffset.Parse("2026-09-20T10:15:30Z");
        var accepted = OrganizationDeploymentAuditRules.TryProject(
            ValidRecord(occurredAt: occurredAt, id: "stable-non-identifying-id"),
            out var item);

        Assert.True(accepted);
        Assert.Equal("stable-non-identifying-id", item!.Id);
        Assert.Equal("control-bff", item.FunctionName);
        Assert.Equal("78bcf459aa11bb22cc33dd44ee55ff6677889900", item.SourceRevision);
        Assert.Equal("production", item.TargetEnvironment);
        Assert.Equal("2026-09-20T10:15:30Z", item.OccurredAt);
        Assert.Equal(["function-only", "control-bff", "no-frontend-publish"], item.ApprovedScope);
        Assert.Equal("succeeded", item.Outcome);
    }

    [Theory]
    [InlineData("cus_123", "control-bff", "78bcf45", "production", "succeeded")]
    [InlineData("ok-id", "ops@elsacloud.app", "78bcf45", "production", "succeeded")]
    [InlineData("ok-id", "control-bff", "ABCDEF0", "production", "succeeded")]
    [InlineData("ok-id", "control-bff", "78bcf45", "live", "succeeded")]
    [InlineData("ok-id", "control-bff", "78bcf45", "production", "ok")]
    public void Projection_drops_invalid_or_leaky_records(
        string id,
        string functionName,
        string sourceRevision,
        string environment,
        string outcome)
    {
        Assert.False(OrganizationDeploymentAuditRules.TryProject(
            ValidRecord(id: id, functionName: functionName, sourceRevision: sourceRevision,
                environment: environment, outcome: outcome),
            out var item));
        Assert.Null(item);
    }

    [Fact]
    public void Projection_rejects_non_utc_timestamps_and_leaky_scope()
    {
        Assert.False(OrganizationDeploymentAuditRules.TryProject(
            ValidRecord(occurredAt: DateTimeOffset.Parse("2026-09-20T12:15:30+02:00")),
            out _));
        Assert.False(OrganizationDeploymentAuditRules.TryProject(
            ValidRecord(scope: ["function-only", "sk_live_secret"]),
            out _));
    }

    private static OrganizationDeploymentAuditRecord ValidRecord(
        DateTimeOffset? occurredAt = null,
        string id = "stable-non-identifying-id",
        string functionName = "control-bff",
        string sourceRevision = "78bcf459aa11bb22cc33dd44ee55ff6677889900",
        string environment = "production",
        string outcome = "succeeded",
        IReadOnlyList<string>? scope = null) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            id,
            functionName,
            sourceRevision,
            environment,
            occurredAt ?? DateTimeOffset.Parse("2026-09-20T10:15:30Z"),
            scope ?? ["function-only", "control-bff", "no-frontend-publish"],
            outcome);
}
