using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>Host settings and legacy borrowed composition inputs copied at application Build.</summary>
/// <remarks>Sequential start/stop is required. Mutating these inputs after Build cannot change the application.</remarks>
public sealed class DatabaseApplicationOptions : HostOptions<DatabaseApplicationContext>
{
    /// <summary>Gets or sets the content root for configuration files; defaults to the application base directory.</summary>
    public FileSystemPath? ContentRootPath { get; set; }

    /// <summary>Gets caller-owned engines to borrow. Nested servers are discovered during Build.</summary>
    public IList<IDatabaseEngine> Engines { get; } = new List<IDatabaseEngine>();

    /// <summary>Gets legacy caller-owned servers to start and stop; their engines are implicitly borrowed.</summary>
    /// <remarks>New composition nests server factories under model engine builders.</remarks>
    public IList<IDatabaseServer> Servers { get; } = new List<IDatabaseServer>();

    /// <summary>Gets caller-owned services, started before servers and stopped after servers drain.</summary>
    public IList<IHostService> Services { get; } = new List<IHostService>();
}
