namespace ElsaControl.Deployment.Azure;

/// <summary>
/// The only provider diagnostics that may cross the runner boundary.  This shared component owns
/// the allowlist because an <see cref="IAzureProviderRunner"/> can be supplied by an adapter or a
/// test; a syntactically valid code is not evidence that it is safe to persist.
/// </summary>
internal static class AzureProviderSafeDiagnostics
{
    public static IReadOnlyList<AzureProviderDiagnostic> Failure(
        AzureProviderRunnerStep step,
        AzureProviderRunnerOutcome outcome,
        string code,
        AzureCommandProcessFailureKind? processFailureKind = null)
    {
        var diagnostics = new List<AzureProviderDiagnostic>(capacity: 3);
        Add(diagnostics, StepOutcomeCode(step, outcome));
        if (Array.IndexOf(PolicyCodes, code) >= 0)
            Add(diagnostics, code);
        if (processFailureKind is { } failureKind)
            Add(diagnostics, ProcessFailureCode(step, failureKind));
        return diagnostics;
    }

    public static IReadOnlyList<AzureProviderDiagnostic> Normalize(
        IReadOnlyList<AzureProviderDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
            return [];

        // Never preserve a caller-provided message.  Unknown codes are dropped rather than
        // trusted merely because they satisfy the general operation-code grammar.
        return diagnostics
            .Where(diagnostic => diagnostic is not null && KnownCodes.Contains(diagnostic.Code))
            .Select(diagnostic => new AzureProviderDiagnostic(diagnostic.Code, diagnostic.Code))
            .Take(20)
            .ToArray();
    }

    private static string StepOutcomeCode(AzureProviderRunnerStep step, AzureProviderRunnerOutcome outcome) =>
        $"azure.step.{StepCode(step)}.{OutcomeCode(outcome)}";

    private static string ProcessFailureCode(AzureProviderRunnerStep step, AzureCommandProcessFailureKind failureKind) =>
        $"azure.step.{StepCode(step)}.process.{FailureKindCode(failureKind)}";

    private static string StepCode(AzureProviderRunnerStep step) => step switch
    {
        AzureProviderRunnerStep.Foundation => "foundation",
        AzureProviderRunnerStep.AcrPull => "acr-pull",
        AzureProviderRunnerStep.SeedSecrets => "seed-secrets",
        AzureProviderRunnerStep.SqlBootstrap => "sql-bootstrap",
        AzureProviderRunnerStep.SqlFirewallCreate => "sql-firewall-create",
        AzureProviderRunnerStep.SqlBootstrapScript => "sql-bootstrap-script",
        AzureProviderRunnerStep.SqlFirewallCleanup => "sql-firewall-cleanup",
        AzureProviderRunnerStep.Workload => "workload",
        AzureProviderRunnerStep.Health => "health",
        AzureProviderRunnerStep.Promotion => "promotion",
        AzureProviderRunnerStep.RestoreStableTraffic => "restore-stable-traffic",
        AzureProviderRunnerStep.Cleanup => "cleanup",
        _ => "provider"
    };

    private static string OutcomeCode(AzureProviderRunnerOutcome outcome) => outcome switch
    {
        AzureProviderRunnerOutcome.Failed => "failed",
        AzureProviderRunnerOutcome.Uncertain => "uncertain",
        _ => "invalid"
    };

    private static string FailureKindCode(AzureCommandProcessFailureKind failureKind) => failureKind switch
    {
        AzureCommandProcessFailureKind.None => "none",
        AzureCommandProcessFailureKind.NonZeroExitCode => "non-zero-exit",
        AzureCommandProcessFailureKind.ExecutableNotFound => "executable-not-found",
        AzureCommandProcessFailureKind.StartFailed => "start-failed",
        AzureCommandProcessFailureKind.TimedOut => "timed-out",
        AzureCommandProcessFailureKind.Cancelled => "cancelled",
        AzureCommandProcessFailureKind.OutputLimitExceeded => "output-limit-exceeded",
        AzureCommandProcessFailureKind.TerminationUncertain => "termination-uncertain",
        AzureCommandProcessFailureKind.InvalidOutput => "invalid-output",
        AzureCommandProcessFailureKind.ExecutionFailed => "execution-failed",
        _ => "unknown"
    };

    private static void Add(List<AzureProviderDiagnostic> diagnostics, string code) =>
        diagnostics.Add(new AzureProviderDiagnostic(code, code));

    private static readonly string[] PolicyCodes =
    [
        "azure.acr.foundation-missing",
        "azure.acr.output-invalid",
        "azure.acr.role-invalid",
        "azure.acr.role-observation-uncertain",
        "azure.acr.role-scope-invalid",
        "azure.acr.scope-invalid",
        "azure.cleanup.completed",
        "azure.cleanup.deployment-invalid",
        "azure.cleanup.deployment-scope-invalid",
        "azure.cleanup.deployment-uncertain",
        "azure.cleanup.group-uncertain",
        "azure.cleanup.identity-invalid",
        "azure.cleanup.identity-observation-uncertain",
        "azure.cleanup.inventory-uncertain",
        "azure.cleanup.ownership-unverified",
        "azure.cleanup.rbac-unverified",
        "azure.cleanup.role-observation-uncertain",
        "azure.cleanup.role-provenance-invalid",
        "azure.cleanup.role-scope-invalid",
        "azure.cleanup.role-uncertain",
        "azure.cleanup.vault-uncertain",
        "azure.foundation.output-invalid",
        "azure.foundation.ownership-invalid",
        "azure.foundation.sql-admin-invalid",
        "azure.foundation.sql-admin-uncertain",
        "azure.health.cancelled",
        "azure.health.unhealthy",
        "azure.health.workload-missing",
        "azure.promotion.candidate-endpoint-invalid",
        "azure.promotion.candidate-not-ready",
        "azure.promotion.candidate-readiness-invalid",
        "azure.promotion.candidate-unready",
        "azure.promotion.health-gate",
        "azure.promotion.health-uncertain",
        "azure.promotion.input-missing",
        "azure.promotion.traffic-invalid",
        "azure.promotion.traffic-uncertain",
        "azure.revision.ambiguous",
        "azure.revision.exhausted",
        "azure.revision.observation-uncertain",
        "azure.rollback.stable-missing",
        "azure.rollback.uncertain",
        "azure.runner.cancelled",
        "azure.runner.input-invalid",
        "azure.runner.scope-invalid",
        "azure.runner.step-invalid",
        "azure.runner.uncertain",
        "azure.secrets.cancelled",
        "azure.secrets.foundation-missing",
        "azure.secrets.inventory-invalid",
        "azure.secrets.name-invalid",
        "azure.secrets.resolve-uncertain",
        "azure.secrets.vault-missing",
        "azure.sql.admin-invalid",
        "azure.sql.admin-verification-uncertain",
        "azure.sql.bootstrap-uncertain",
        "azure.sql.cancelled",
        "azure.sql.firewall-uncertain",
        "azure.sql.foundation-missing",
        "azure.sql.output-missing",
        "azure.traffic.ambiguous",
        "azure.traffic.observation-uncertain",
        "azure.traffic.unhealthy",
        "azure.workload.foundation-missing",
        "azure.workload.output-invalid"
    ];

    private static readonly HashSet<string> KnownCodes = CreateKnownCodes();

    private static HashSet<string> CreateKnownCodes()
    {
        var codes = new HashSet<string>(PolicyCodes, StringComparer.Ordinal);
        foreach (var step in Enum.GetValues<AzureProviderRunnerStep>())
        {
            foreach (var outcome in new[] { AzureProviderRunnerOutcome.Failed, AzureProviderRunnerOutcome.Uncertain })
                codes.Add(StepOutcomeCode(step, outcome));
            foreach (var failureKind in Enum.GetValues<AzureCommandProcessFailureKind>())
                codes.Add(ProcessFailureCode(step, failureKind));
        }
        return codes;
    }
}
