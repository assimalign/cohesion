namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The default <see cref="IExecutableArtifact"/>: a resolved executable path on disk.
/// </summary>
internal sealed class ExecutableArtifact : IExecutableArtifact
{
    public ExecutableArtifact(ResourceId resource, string executablePath)
    {
        Resource = resource;
        ExecutablePath = executablePath;
    }

    public ResourceId Resource { get; }

    public string ExecutablePath { get; }
}
