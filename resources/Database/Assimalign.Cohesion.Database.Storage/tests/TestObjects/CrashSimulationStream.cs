using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A stream that models crash semantics: bytes written are "in the OS page cache"
/// and survive a crash only if they were flushed. <see cref="CaptureDurable"/>
/// returns what would remain on disk after a power loss at that instant.
/// </summary>
/// <remarks>
/// <para>
/// In write-through mode every write is immediately durable — the worst case for a
/// steal-capable buffer pool, where the OS persists stolen page writes before the
/// transaction resolves. In the default mode only <see cref="Flush"/> makes prior
/// writes durable, which models losing journal appends that were never fsynced.
/// </para>
/// <para>
/// <b>Torn writes.</b> Streams that share a <see cref="CrashPoint"/> lose power together at
/// one scheduled write (counting every write and length change across them). In write-through
/// mode the scheduled write is torn: only a durable prefix of
/// <see cref="CrashPoint.DurableSectors"/> whole 512-byte sectors of it reaches the media,
/// the rest of its range keeps its old bytes — the old-or-new sector model PostgreSQL's
/// full-page writes and every double-buffered header assume. In flush-gated mode the write
/// never reaches the media. Every later write, length change or flush on a stream sharing the
/// point throws <see cref="SimulatedPowerLossException"/> and changes nothing durable: the
/// process is gone.
/// </para>
/// </remarks>
public sealed class CrashSimulationStream : IFileSystemFileHandle
{
    /// <summary>The sector size torn writes are cut at.</summary>
    public const int SectorSize = 512;

    private readonly MemoryStream _live = new();
    private readonly bool _writeThrough;
    private readonly CrashPoint? _crashPoint;
    private readonly string _name;
    private byte[] _durable = Array.Empty<byte>();

    /// <summary>
    /// Initializes a crash-simulation stream.
    /// </summary>
    /// <param name="writeThrough">
    /// When true, every write is immediately durable (worst-case steal); when false,
    /// writes become durable only on flush.
    /// </param>
    /// <param name="crashPoint">The scheduled power loss this stream shares, if any.</param>
    /// <param name="name">The stream's name in the crash point's write log.</param>
    public CrashSimulationStream(bool writeThrough = false, CrashPoint? crashPoint = null, string name = "stream")
    {
        _writeThrough = writeThrough;
        _crashPoint = crashPoint;
        _name = name;
    }

    /// <summary>
    /// Initializes a crash-simulation stream over existing durable content.
    /// </summary>
    /// <param name="content">The initial durable bytes.</param>
    /// <param name="writeThrough">Write-through durability mode.</param>
    /// <param name="crashPoint">The scheduled power loss this stream shares, if any.</param>
    /// <param name="name">The stream's name in the crash point's write log.</param>
    public CrashSimulationStream(byte[] content, bool writeThrough = false, CrashPoint? crashPoint = null, string name = "stream")
        : this(writeThrough, crashPoint, name)
    {
        _live.Write(content);
        _live.Position = 0;
        _durable = (byte[])content.Clone();
    }

    /// <summary>
    /// Gets the number of flush calls observed (durability points).
    /// </summary>
    public int FlushCount { get; private set; }

    /// <summary>
    /// Returns the bytes that would survive a crash (power loss) right now.
    /// </summary>
    public byte[] CaptureDurable() => (byte[])_durable.Clone();

    /// <summary>
    /// Returns the live (post-crash-lost) content, for assertions on what was pending.
    /// </summary>
    public byte[] CaptureLive() => _live.ToArray();

    /// <inheritdoc />
    public bool SupportsDurableFlush => true;

    /// <inheritdoc />
    public long Length => _live.Length;

    /// <inheritdoc />
    public void Flush(bool durable = false)
    {
        _crashPoint?.ThrowIfCrashed();

        // Preserve the fixture's flush-gated persistence, including ordinary
        // flushes. Durable requests now have an explicit, simulated contract.
        FlushCount++;
        _durable = _live.ToArray();
    }

    /// <inheritdoc />
    public int Read(Span<byte> buffer, long offset)
    {
        _live.Position = offset;
        return _live.Read(buffer);
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span, offset));
    }

    /// <inheritdoc />
    public void SetLength(long value)
    {
        if (_crashPoint is not null && _crashPoint.Next(_name, "SetLength", value, 0))
        {
            // Power is lost before the length change reaches the media.
            throw new SimulatedPowerLossException();
        }

        _live.SetLength(value);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        if (_crashPoint is not null && _crashPoint.Next(_name, "Write", offset, buffer.Length))
        {
            if (_writeThrough)
            {
                int durable = Math.Min(buffer.Length, _crashPoint.DurableSectors * SectorSize);
                if (durable > 0)
                {
                    long end = offset + durable;
                    if (_durable.Length < end)
                    {
                        Array.Resize(ref _durable, (int)end);
                    }

                    buffer[..durable].CopyTo(_durable.AsSpan((int)offset));
                }
            }

            throw new SimulatedPowerLossException();
        }

        _live.Position = offset;
        _live.Write(buffer);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span, offset);
        return default;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush(durable);
        return default;
    }

    /// <inheritdoc />
    public void Dispose() => _live.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _live.DisposeAsync();
}

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

/// <summary>
/// The failure every write raises once a <see cref="CrashPoint"/> lost power: the process the
/// storage runs in is gone, and nothing it does afterwards reaches the media.
/// </summary>
public sealed class SimulatedPowerLossException : IOException
{
    /// <summary>
    /// Initializes a new <see cref="SimulatedPowerLossException"/>.
    /// </summary>
    public SimulatedPowerLossException()
        : base("Simulated power loss.")
    {
    }

    /// <summary>
    /// Runs an action a simulated power loss interrupts, and returns the power loss: thrown as
    /// itself, or as the cause of the <see cref="StorageOfflineException"/> a header slot write
    /// that failed takes its storage offline with (#1268). Either way the process is gone; the
    /// test reopens what the media holds.
    /// </summary>
    /// <param name="action">The action that loses power.</param>
    /// <param name="context">What the test was doing, for the failure message.</param>
    /// <returns>The power loss.</returns>
    /// <exception cref="InvalidOperationException">The action completed; any other failure propagates.</exception>
    public static SimulatedPowerLossException Expect(Action action, string? context = null)
    {
        try
        {
            action();
        }
        catch (SimulatedPowerLossException loss)
        {
            return loss;
        }
        catch (StorageOfflineException offline) when (offline.InnerException is SimulatedPowerLossException loss)
        {
            return loss;
        }

        throw new InvalidOperationException($"Expected a simulated power loss, but the action completed{(context is null ? "." : $": {context}.")}");
    }
}
