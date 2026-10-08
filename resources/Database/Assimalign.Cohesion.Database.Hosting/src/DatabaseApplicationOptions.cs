using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>Host settings and legacy borrowed composition inputs copied at application Build.</summary>
/// <remarks>Sequential start/stop is required. Mutating these inputs after Build cannot change the application.</remarks>
public sealed class DatabaseApplicationOptions : HostOptions<DatabaseApplicationContext>
{
    /// <summary>
    /// The longest <see cref="ReopenMaximumDelay"/> accepted: the longest a timer waits
    /// (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).
    /// </summary>
    public static readonly TimeSpan MaximumReopenDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Gets or sets the content root for configuration files; defaults to the application base
    /// directory. Read, with the environment, when the builder is created
    /// (<see cref="DatabaseApplicationBuilder.Environment"/>), as an enabled resource's ambient
    /// context sets both then.
    /// </summary>
    public FileSystemPath? ContentRootPath { get; set; }

    /// <summary>Gets caller-owned engines to borrow. Nested servers are discovered during Build.</summary>
    public IList<DatabaseEngine> Engines { get; } = new List<DatabaseEngine>();

    /// <summary>Gets legacy caller-owned servers to start and stop; their engines are implicitly borrowed.</summary>
    /// <remarks>New composition nests server factories under model engine builders.</remarks>
    public IList<DatabaseServer> Servers { get; } = new List<DatabaseServer>();

    /// <summary>Gets caller-owned services, started before servers and stopped after servers drain.</summary>
    public IList<IHostService> Services { get; } = new List<IHostService>();

    /// <summary>
    /// Gets or sets whether the running application reopens a database its engine reports offline
    /// (owner decision 22 of 2026-10-06): true, the default, retries
    /// <see cref="DatabaseEngine.OpenDatabaseAsync"/> with exponential backoff and jitter, from
    /// <see cref="ReopenInitialDelay"/> up to <see cref="ReopenMaximumDelay"/>, until the reopen
    /// succeeds or the application stops; false leaves every offline database to the operator.
    /// </summary>
    /// <remarks>
    /// The application's health is unhealthy, naming each offline database and its cause, either
    /// way, until the database is open again. A database dropped meanwhile is never reopened.
    /// </remarks>
    public bool ReopenOfflineDatabases { get; set; } = true;

    /// <summary>
    /// Gets or sets how long the application waits before its first reopen of a database it found
    /// offline, and the first step of the backoff after a failed reopen: one second, the engines'
    /// worker failure backoff, by default. Each attempt waits a random time between half the step
    /// and the step. Must be positive.
    /// </summary>
    public TimeSpan ReopenInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the longest step of the reopen backoff: the step doubles after each failed
    /// reopen of a database until it reaches this. One minute by default. Must be at least
    /// <see cref="ReopenInitialDelay"/> and at most <see cref="MaximumReopenDelay"/>.
    /// </summary>
    public TimeSpan ReopenMaximumDelay { get; set; } = TimeSpan.FromMinutes(1);
}
