using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace ElsaControl.SharedShellAc1;

/// <summary>
/// One collectible ALC per verified package-set revision, shared across tenant-group shells
/// and unloaded when the last shell releases it (Architect Q2 on #798).
/// </summary>
public sealed class PackageRevisionLoadContext : AssemblyLoadContext
{
    private int _refCount;
    private Assembly? _assembly;
    private WeakReference? _weakSelf;
    private WeakReference? _weakAssembly;

    public PackageRevisionLoadContext(string revisionHash)
        : base($"verified-package:{revisionHash}", isCollectible: true)
    {
        RevisionHash = revisionHash;
    }

    public string RevisionHash { get; }
    public string PackagePath { get; private set; } = "";
    public string ContentSha512 { get; private set; } = "";
    public Assembly Assembly => _assembly ?? throw new InvalidOperationException("Package has not been loaded.");
    public int RefCount => _refCount;

    public Assembly LoadPackage(string packagePath)
    {
        PackagePath = packagePath;
        var bytes = File.ReadAllBytes(packagePath);
        ContentSha512 = Convert.ToHexString(SHA512.HashData(bytes));
        using var stream = new MemoryStream(bytes, writable: false);
        _assembly = LoadFromStream(stream);
        _weakSelf = new WeakReference(this);
        _weakAssembly = new WeakReference(_assembly);
        return _assembly;
    }

    public void AddRef() => Interlocked.Increment(ref _refCount);

    public bool Release()
    {
        var remaining = Interlocked.Decrement(ref _refCount);
        if (remaining > 0)
            return false;
        if (remaining < 0)
            throw new InvalidOperationException("Package load-context refcount went negative.");

        _assembly = null;
        Unload();
        return true;
    }

    public WeakReference WeakSelf => _weakSelf ?? throw new InvalidOperationException("Package has not been loaded.");
    public WeakReference WeakAssembly => _weakAssembly ?? throw new InvalidOperationException("Package has not been loaded.");

    protected override Assembly? Load(AssemblyName assemblyName) => null;
}

internal static class PackageRevisionLoader
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static PackageRevisionLoadContext Load(string packagePath, string revisionHash)
    {
        var context = new PackageRevisionLoadContext(revisionHash);
        context.LoadPackage(packagePath);
        return context;
    }
}
