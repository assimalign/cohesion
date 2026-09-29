namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one <c>&lt;source&gt;:&lt;key&gt;</c> mount source a gateway asks an
/// <see cref="IResourceSourceProvider"/> to resolve.
/// </summary>
/// <param name="Application">The application that owns the consuming resource.</param>
/// <param name="Consumer">The resource whose mount is being resolved.</param>
/// <param name="Mount">The consuming resource's mount name.</param>
/// <param name="Kind">
/// The mount kind. <see cref="ResourceMountKind.Secret"/> selects
/// <see cref="IResourceSourceProvider.ReadSecretAsync"/> (or
/// <see cref="IResourceSourceProvider.ReadCertificateAsync"/> for an endpoint certificate mount);
/// <see cref="ResourceMountKind.Configuration"/> selects
/// <see cref="IResourceSourceProvider.ReadConfigurationAsync"/>.
/// </param>
/// <param name="Key">The <c>&lt;key&gt;</c> part of the mount source.</param>
/// <param name="Store">
/// The connection to the model resource named by the <c>&lt;source&gt;</c> part, or
/// <see langword="null"/> for a source outside the model, whose provider authenticates itself.
/// </param>
public sealed record ResourceSourceRequest(
    ApplicationName Application,
    ResourceName Consumer,
    string Mount,
    ResourceMountKind Kind,
    string Key,
    ResourceProviderConnection? Store);
