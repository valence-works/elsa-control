namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// A Pulumi stack identity. Names are canonicalised to Pulumi's allowed charset
/// before they enter the module.
/// </summary>
public sealed record StackIdentity
{
    public StackIdentity(string project, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Project = project;
        Name = name;
    }

    public string Project { get; }

    public string Name { get; }

    public string Key => $"{Project}/{Name}";
}
