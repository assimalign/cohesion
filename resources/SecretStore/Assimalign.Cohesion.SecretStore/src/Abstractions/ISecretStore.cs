using System;

namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// Represents an opened secret store whose resources are released when it is disposed.
/// </summary>
/// <remarks>
/// Storage, versioning, and policy operations are supplied by SecretStore feature contracts. This
/// area-root interface provides the common lifetime boundary without coupling those features to the
/// hosting implementation.
/// </remarks>
public interface ISecretStore : IDisposable
{
}
