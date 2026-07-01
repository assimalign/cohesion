namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A deployable artifact that is a resolved executable on disk — the form produced by a
/// local gateway.
/// </summary>
public interface IExecutableArtifact : IResourceArtifact
{
    /// <summary>
    /// The absolute path to the resolved executable.
    /// </summary>
    string ExecutablePath { get; }
}
