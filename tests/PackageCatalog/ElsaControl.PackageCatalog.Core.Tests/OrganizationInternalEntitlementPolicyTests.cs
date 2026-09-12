using System.Security.Cryptography;
using System.Text;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class OrganizationInternalEntitlementPolicyTests
{
    private const string ValidReason = "Internal dogfood of managed hosting";
    private const string ValidExpiry = "2026-10-12T10:00:00Z";
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FixedTimeProvider _clock = new(Now);
    private readonly RecordingStore _store = new();
    private readonly OrganizationInternalEntitlementService _service;

    public OrganizationInternalEntitlementPolicyTests() => _service = new(_store, _clock);

    [Fact]
    public void Valid_input_is_normalized_to_trimmed_reason_and_utc_expiry()
    {
        var errors = Validate("2026-10-12T10:00:00.1234567Z", out var terms, reason: $"  {ValidReason}  ", maxInstances: 3);

        Assert.Empty(errors);
        Assert.Equal(new OrganizationInternalEntitlementTerms(ValidReason, 3, Now.AddDays(30).AddTicks(1234567)), terms);
        Assert.Equal(TimeSpan.Zero, terms!.ExpiresAt.Offset);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("  abc  ")]
    [InlineData("200")]
    public void Reason_length_bounds_are_inclusive_after_trimming(string reason)
    {
        Assert.Empty(Validate(ValidExpiry, out _, reason: reason == "200" ? new string('r', 200) : reason));
    }

    [Theory]
    [InlineData(null, "Reason is required.")]
    [InlineData("   ", "Reason is required.")]
    [InlineData(" ab ", "Reason must be between 3 and 200 characters.")]
    [InlineData("201", "Reason must be between 3 and 200 characters.")]
    [InlineData("marker\u0007bell", "Reason must not contain control or formatting characters.")]
    [InlineData("marker\u202Ebidi-override", "Reason must not contain control or formatting characters.")]
    [InlineData("marker\u200Bzero-width", "Reason must not contain control or formatting characters.")]
    [InlineData("marker\u2028line-separator", "Reason must not contain control or formatting characters.")]
    public void Unsafe_or_out_of_bounds_reasons_are_rejected_without_echo(string? reason, string expected)
    {
        reason = reason == "201" ? "marker" + new string('r', 195) : reason;

        var errors = Validate(ValidExpiry, out var terms, reason: reason);

        Assert.Null(terms);
        Assert.Equal([expected], errors[OrganizationInternalEntitlementPolicy.ReasonField]);
        Assert.DoesNotContain(errors.Values.SelectMany(x => x), message => message.Contains("marker", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "MaxInstances is required.")]
    [InlineData(0, "MaxInstances must be between 1 and 3.")]
    [InlineData(4, "MaxInstances must be between 1 and 3.")]
    [InlineData(-1, "MaxInstances must be between 1 and 3.")]
    public void Instance_cap_is_bounded(int? maxInstances, string expected)
    {
        var errors = Validate(ValidExpiry, out _, maxInstances: maxInstances);

        Assert.Equal([expected], errors[OrganizationInternalEntitlementPolicy.MaxInstancesField]);
    }

    [Theory]
    [InlineData(null, "ExpiresAt is required.")]
    [InlineData("  ", "ExpiresAt is required.")]
    [InlineData("next month", "ExpiresAt must be an ISO-8601 UTC timestamp.")]
    [InlineData("2026-10-01T00:00:00", "ExpiresAt must be an ISO-8601 UTC timestamp.")]
    [InlineData("2026-10-01T00:00:00+02:00", "ExpiresAt must be an ISO-8601 UTC timestamp.")]
    [InlineData("2026-09-12T10:00:00Z", "ExpiresAt must be in the future.")]
    [InlineData("2026-09-12T09:59:59Z", "ExpiresAt must be in the future.")]
    [InlineData("2026-12-11T10:00:01Z", "ExpiresAt must be at most 90 days ahead.")]
    public void Expiry_must_be_explicit_utc_in_the_future_and_within_ninety_days(string? expiresAt, string expected)
    {
        var errors = Validate(expiresAt, out _);

        Assert.Equal([expected], errors[OrganizationInternalEntitlementPolicy.ExpiresAtField]);
    }

    [Theory]
    [InlineData("2026-12-11T10:00:00Z")]
    [InlineData("2026-12-11T10:00:00+00:00")]
    [InlineData("2026-09-12T10:00:00.0000001Z")]
    public void Expiry_window_is_inclusive_for_explicit_utc(string expiresAt)
    {
        Assert.Empty(Validate(expiresAt, out _));
    }

    [Fact]
    public void Persistence_guard_rejects_terms_outside_the_bounds()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            OrganizationInternalEntitlementPolicy.EnsureValid(new(ValidReason, 4, Now.AddDays(91)), Now));

        Assert.Contains("MaxInstances must be between 1 and 3.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ExpiresAt must be at most 90 days ahead.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Operator_subject_is_recorded_as_a_one_way_fingerprint()
    {
        var expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("api-key")));

        Assert.Equal(expected, OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(" api-key "));
        Assert.Null(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("  "));
    }

    [Fact]
    public async Task Service_validates_and_persists_against_the_injected_clock()
    {
        var result = await _service.GrantAsync(Guid.NewGuid(), ValidReason, 2, ValidExpiry, "operator");

        Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, result.Outcome);
        Assert.Equal(Now, _store.Now);
        Assert.Equal(new OrganizationInternalEntitlementTerms(ValidReason, 2, Now.AddDays(30)), _store.Grant!.Terms);
        Assert.Equal("operator", _store.Grant.OperatorSubject);
    }

    [Fact]
    public async Task Service_rejects_an_expiry_the_injected_clock_has_passed_without_calling_the_store()
    {
        _clock.UtcNow = Now.AddDays(31);

        var result = await _service.GrantAsync(Guid.NewGuid(), ValidReason, 2, ValidExpiry, "operator");

        Assert.Equal(OrganizationInternalEntitlementOutcome.Invalid, result.Outcome);
        Assert.Equal(["ExpiresAt must be in the future."], result.Errors![OrganizationInternalEntitlementPolicy.ExpiresAtField]);
        Assert.Null(_store.Grant);
    }

    [Fact]
    public async Task Service_reads_and_revokes_with_the_injected_clock()
    {
        var organizationId = Guid.NewGuid();
        _clock.UtcNow = Now.AddHours(5);
        await _service.GetAsync(organizationId);
        Assert.Equal(Now.AddHours(5), _store.Now);

        _clock.UtcNow = Now.AddHours(6);
        await _service.RevokeAsync(organizationId, "operator");

        Assert.Equal(Now.AddHours(6), _store.Now);
        Assert.Equal("operator", _store.RevokedBy);
    }

    private static IReadOnlyDictionary<string, string[]> Validate(
        string? expiresAt,
        out OrganizationInternalEntitlementTerms? terms,
        string? reason = ValidReason,
        int? maxInstances = 2) =>
        OrganizationInternalEntitlementPolicy.Validate(reason, maxInstances, expiresAt, Now, out terms);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RecordingStore : IOrganizationInternalEntitlementStore
    {
        public DateTimeOffset? Now { get; private set; }
        public OrganizationInternalEntitlementGrant? Grant { get; private set; }
        public string? RevokedBy { get; private set; }

        public Task<OrganizationInternalEntitlementResult> GetInternalEntitlementAsync(Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Record(now, OrganizationInternalEntitlementOutcome.Current);

        public Task<OrganizationInternalEntitlementResult> GrantInternalEntitlementAsync(OrganizationInternalEntitlementGrant grant, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Grant = grant;
            return Record(now, OrganizationInternalEntitlementOutcome.Granted);
        }

        public Task<OrganizationInternalEntitlementResult> RevokeInternalEntitlementAsync(Guid organizationId, string? operatorSubject, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            RevokedBy = operatorSubject;
            return Record(now, OrganizationInternalEntitlementOutcome.Revoked);
        }

        private Task<OrganizationInternalEntitlementResult> Record(DateTimeOffset now, OrganizationInternalEntitlementOutcome outcome)
        {
            Now = now;
            return Task.FromResult(new OrganizationInternalEntitlementResult(outcome));
        }
    }
}
