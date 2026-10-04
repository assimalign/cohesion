using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// A memory stream whose writes and truncations fail on demand: a write can fail before
/// it writes anything, or tear (write the first half of its bytes, then fail), and a
/// <see cref="SetLength"/> can fail. Its bytes stay readable through <see cref="MemoryStream.ToArray"/>
/// after it is disposed, so a test can reopen what a closed storage left behind.
/// </summary>
internal sealed class FaultingMemoryStream : MemoryStream
{
    private int _skipWrites;
    private int _failWrites;
    private int _tearWrites;
    private int _failTruncations;

    /// <summary>
    /// Gets or sets the number of upcoming writes that succeed before <see cref="FailWrites"/> and
    /// <see cref="TearWrites"/> apply.
    /// </summary>
    internal int SkipWrites
    {
        get => Volatile.Read(ref _skipWrites);
        set => Volatile.Write(ref _skipWrites, value);
    }

    /// <summary>Gets or sets the number of upcoming writes that fail before writing anything.</summary>
    internal int FailWrites
    {
        get => Volatile.Read(ref _failWrites);
        set => Volatile.Write(ref _failWrites, value);
    }

    /// <summary>Gets or sets the number of upcoming writes that write half their bytes, then fail.</summary>
    internal int TearWrites
    {
        get => Volatile.Read(ref _tearWrites);
        set => Volatile.Write(ref _tearWrites, value);
    }

    /// <summary>Gets or sets the number of upcoming <see cref="SetLength"/> calls that fail.</summary>
    internal int FailTruncations
    {
        get => Volatile.Read(ref _failTruncations);
        set => Volatile.Write(ref _failTruncations, value);
    }

    // Every write funnels through the array overload: a MemoryStream subclass's span overload
    // calls back into it, so a span override that called the base would recurse.

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

    /// <inheritdoc />
    public override void WriteByte(byte value) => Write([value], 0, 1);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (TrySpend(ref _skipWrites))
        {
            base.Write(buffer, offset, count);
            return;
        }

        if (TrySpend(ref _failWrites))
        {
            throw new IOException("Injected write failure.");
        }

        if (TrySpend(ref _tearWrites))
        {
            base.Write(buffer, offset, count / 2);
            throw new IOException("Injected torn write.");
        }

        base.Write(buffer, offset, count);
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        if (TrySpend(ref _failTruncations))
        {
            throw new IOException("Injected truncation failure.");
        }

        base.SetLength(value);
    }

    private static bool TrySpend(ref int budget)
    {
        int current;
        do
        {
            current = Volatile.Read(ref budget);
            if (current <= 0)
            {
                return false;
            }
        }
        while (Interlocked.CompareExchange(ref budget, current - 1, current) != current);

        return true;
    }
}
