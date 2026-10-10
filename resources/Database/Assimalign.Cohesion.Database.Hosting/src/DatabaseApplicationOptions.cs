using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>Host settings, host policy only (timeouts and the reopen policy), copied at application Build.</summary>
/// <remarks>
/// Sequential start/stop is required. Mutating these settings after Build cannot change the
/// application. The borrowed <c>Engines</c>, <c>Servers</c> and <c>Services</c> lists were deleted
/// (owner decision 54 of 2026-10-09): engines and services register on the builder
/// (<see cref="DatabaseApplicationBuilder.AddEngine(DatabaseEngine)"/>,
/// <see cref="DatabaseApplicationBuilder.AddService(IHostService)"/>), and servers on their
/// engine's builder.
/// </remarks>
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
