using System.Text.RegularExpressions;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// Production SQL Server rejects a user-initiated transaction that does not run inside its retrying
/// execution strategy. Catalog persistence therefore opens transactions only through
/// <c>CatalogDbContextTransactionExtensions</c>, and every persistence test context enforces the rule.
/// </summary>
public sealed class CatalogTransactionBoundaryTests
{
    private const string PersistenceSource = "src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore";
    private const string PersistenceTests = "tests/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests";
    private static readonly Regex BeginTransactionCall = new(@"\bBeginTransaction(Async)?\s*\(");
    private static readonly Regex UseSqliteCall = new(@"\.UseSqlite\s*\(");
    private readonly DirectoryInfo _repoRoot = FindRepoRoot();

    [Fact]
    public void Only_the_transaction_helper_begins_a_catalog_transaction()
    {
        var calls = FindCalls(PersistenceSource, BeginTransactionCall);

        Assert.Equal(["CatalogDbContextTransactionExtensions.cs:1"], calls.GroupBy(x => x).Select(x => $"{x.Key}:{x.Count()}"));
    }

    [Fact]
    public void Every_persistence_test_context_uses_the_enforcing_execution_strategy()
    {
        var calls = FindCalls(PersistenceTests, UseSqliteCall);

        Assert.All(calls, file => Assert.Equal("RetryingSqliteTestOptions.cs", file));
    }

    private List<string> FindCalls(string relativeDirectory, Regex call)
    {
        var directory = Path.Combine(_repoRoot.FullName, relativeDirectory);
        Assert.True(Directory.Exists(directory), $"Missing source directory {directory}.");
        return Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(directory, path)))
            .SelectMany(path => call.Matches(File.ReadAllText(path)).Select(_ => Path.GetFileName(path)))
            .ToList();
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar)[0] is "bin" or "obj";

    private static DirectoryInfo FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;

        return directory ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
