namespace ElsaControl.SharedShellAc1.Tests;

public sealed class CollectibleUnloadTests
{
    [Fact]
    public void Shared_revision_alc_unloads_after_last_shell_releases_it()
    {
        var result = UnloadMeasurement.Run(FindPackage());
        Assert.True(result.ContextCollected, "Collectible AssemblyLoadContext was still rooted after unload.");
        Assert.True(result.AssemblyCollected, "Collectible package assembly was still rooted after unload.");
        Assert.Equal(1, result.RefCountAtUnload);
    }

    private static string FindPackage()
    {
        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "verified-package", "bin", "Release", "net10.0", "ElsaControl.SharedShellAc1.VerifiedPackage.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "verified-package", "bin", "Debug", "net10.0", "ElsaControl.SharedShellAc1.VerifiedPackage.dll")),
        };
        var found = roots.FirstOrDefault(File.Exists);
        if (found is null)
            throw new FileNotFoundException("Build verified-package first (see scripts/shared-shell-ac1/run.sh).");
        return found;
    }
}
