using System.Text.RegularExpressions;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Core.Tests;

internal static class ManagedElsaReasonLiteralScanner
{
    /// <summary>
    /// Park-reason families the completeness scan must see. A new family
    /// cannot land in the catalog or as a quoted production write unseen.
    /// </summary>
    internal static readonly string[] ReasonFamilyPrefixes =
    [
        "provider.submission.",
        "provider.reconciliation.",
        "provider.identity-binding",
        "azure.deployment.",
        "azure.recovery.",
        "staging.lever.",
        "deletion.",
        "lifecycle.deletion.",
        "assignment.rebind."
    ];

    /// <summary>
    /// Park codes whose family also contains non-park runner vocabulary.
    /// Keep these exact so <c>azure.promotion.candidate-*</c> stays out.
    /// </summary>
    internal static readonly string[] ReasonExactCodes =
    [
        ManagedElsaReasonCodeCatalog.AzurePromotionUncertain,
        ManagedElsaReasonCodeCatalog.AzurePromotionRollbackUncertain
    ];

    internal static readonly Regex ProductionReasonLiteral = new(
        BuildPattern(),
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsCataloguedFamily(string code) =>
        ReasonFamilyPrefixes.Any(prefix => code.StartsWith(prefix, StringComparison.Ordinal)) ||
        ReasonExactCodes.Contains(code, StringComparer.Ordinal);

    internal static IReadOnlyList<string> FindUncatalogued(string sourceText)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in ProductionReasonLiteral.Matches(sourceText))
        {
            var code = match.Groups[1].Value;
            if (!ManagedElsaReasonCodeCatalog.TryGet(code, out _))
                missing.Add(code);
        }

        return missing.ToArray();
    }

    internal static IReadOnlyList<string> FindUncataloguedInProduction(string repoRoot)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith("ManagedElsaReasonCodeCatalog.cs", StringComparison.Ordinal))
                continue;

            foreach (var code in FindUncatalogued(File.ReadAllText(file)))
                missing.Add($"{code} ({Path.GetRelativePath(repoRoot, file)})");
        }

        return missing.ToArray();
    }

    private static string BuildPattern()
    {
        var families = string.Join("|", ReasonFamilyPrefixes.Select(prefix =>
            Regex.Escape(prefix) + "[a-z0-9.-]*"));
        var exacts = string.Join("|", ReasonExactCodes.Select(Regex.Escape));
        return "\"(" + families + "|" + exacts + ")\"";
    }
}
