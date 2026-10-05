using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A thread-safe, ordered record of the steps the base-suite doubles take, so a test can assert
/// the order the bases drive them in.
/// </summary>
internal sealed class TestLog
{
    private readonly object _sync = new();
    private readonly List<string> _entries = [];

    /// <summary>Appends a step.</summary>
    /// <param name="entry">The step.</param>
    public void Add(string entry)
    {
        lock (_sync)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>Gets a snapshot of the steps, in the order they were taken.</summary>
    public string[] Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }
}
