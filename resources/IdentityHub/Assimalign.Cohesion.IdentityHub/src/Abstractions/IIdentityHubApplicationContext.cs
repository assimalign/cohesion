using System;
using System.IO;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Describes the IdentityHub application context without hosting dependencies.
/// </summary>
public interface IIdentityHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }

    /// <summary>
    /// Gets the hub's directory of identity objects: the ones the application builder defined,
    /// served locked, over the ones in the application's store.
    /// </summary>
    /// <exception cref="InvalidOperationException">The application has not been built.</exception>
    IIdentityHubDirectory Directory { get; }
}
