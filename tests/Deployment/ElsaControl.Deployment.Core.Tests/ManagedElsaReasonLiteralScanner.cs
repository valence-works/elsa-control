using System.Text.RegularExpressions;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Core.Tests;

internal static class ManagedElsaReasonLiteralScanner
{
    /// <summary>
    /// Production park-reason families. Keep this aligned with
    /// <see cref="ManagedElsaReasonCodeCatalog.DefinedCodes"/> so a new
    /// family cannot land in the catalog (or in src) unseen.
    /// </summary>
    internal static readonly Regex ProductionReasonLiteral = new(
        """"(provider\.(?:submission|reconciliation|identity-binding)[a-z0-9.-]*|azure\.deployment\.[a-z0-9.-]+|azure\.recovery\.[a-z0-9.-]+|azure\.promotion\.(?:uncertain|rollback-uncertain)|staging\.lever\.[a-z0-9.-]+)"""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
}
