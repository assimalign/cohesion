using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A power loss scheduled at one write across the <see cref="CrashSimulationStream"/>s that
/// share it. Writes and length changes are counted from 1 across all of them; with
/// <see cref="CrashAtWrite"/> zero the point only counts and logs them.
/// </summary>
public sealed class CrashPoint
{
    private readonly object _sync = new();
    private readonly List<string> _log = new();

    /// <summary>
    /// Gets or sets the write (1-based, across every sharing stream) at which power is lost;
    /// zero schedules none.
    /// </summary>
    public int CrashAtWrite { get; set; }

    /// <summary>
    /// Gets or sets a condition that schedules the power loss at the first write it accepts:
    /// given the stream's name, the operation (<c>Write</c> or <c>SetLength</c>), the offset
    /// (or new length) and the byte count. Checked in addition to <see cref="CrashAtWrite"/>.
    /// </summary>
    public Func<string, string, long, int, bool>? CrashWhen { get; set; }

    /// <summary>
    /// Gets or sets how many whole 512-byte sectors of the scheduled write reach the media
    /// first, on a write-through stream.
    /// </summary>
    public int DurableSectors { get; set; }

    /// <summary>
    /// Gets the number of writes and length changes observed so far.
    /// </summary>
    public int Writes
    {
        get
        {
            lock (_sync)
            {
                return _log.Count;
            }
        }
    }

    /// <summary>
    /// Gets whether the scheduled power loss has happened.
    /// </summary>
    public bool HasCrashed { get; private set; }

    /// <summary>
    /// Gets a description of each write observed, in order: the stream, the operation, the
    /// offset (or new length) and the byte count.
    /// </summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_sync)
            {
                return _log.ToArray();
            }
        }
    }

    /// <summary>
    /// Throws when power was lost: nothing more reaches any stream sharing this point.
    /// </summary>
    internal void ThrowIfCrashed()
    {
        if (HasCrashed)
        {
            throw new SimulatedPowerLossException();
        }
    }

    /// <summary>
    /// Counts one write; returns true when it is the scheduled one (power is lost during it).
    /// </summary>
    internal bool Next(string stream, string operation, long offset, int count)
    {
        lock (_sync)
        {
            ThrowIfCrashed();
            _log.Add($"{stream} {operation} {offset} {count}");
            if ((CrashAtWrite > 0 && _log.Count == CrashAtWrite) || CrashWhen?.Invoke(stream, operation, offset, count) == true)
            {
                HasCrashed = true;
                return true;
            }

            return false;
        }
    }
}
