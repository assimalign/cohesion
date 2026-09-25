using System.IO;

namespace Assimalign.Cohesion.ApiManager;

/// <summary>
/// Describes the ApiManager application context without hosting dependencies.
/// </summary>
public interface IApiManagerApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
