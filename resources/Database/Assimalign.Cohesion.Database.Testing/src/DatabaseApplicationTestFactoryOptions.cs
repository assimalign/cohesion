using System;

namespace Assimalign.Cohesion.Database.Testing;

/// <summary>
/// Options controlling a <see cref="DatabaseApplicationTestFactory"/> invocation.
/// </summary>
public sealed class DatabaseApplicationTestFactoryOptions
{
    /// <summary>Gets or sets the command-line arguments passed to the program's entry point.</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>
    /// Gets or sets how long disposal waits for the program to return after its shutdown is
    /// signaled. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
