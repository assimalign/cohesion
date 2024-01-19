using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Net;

public struct CorrelationId : IEquatable<CorrelationId>
{
    // Base32 encoding - in ascii sort order for easy text based sorting
    private const string encoding = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

    // Seed the _lastConnectionId for this application instance with
    // the number of 100-nanosecond intervals that have elapsed since 12:00:00 midnight, January 1, 0001
    // for a roughly increasing _lastId over restarts
    private static long increment = DateTime.UtcNow.Ticks;
    private static CorrelationId correlationId;
    private string currentId;

    public CorrelationId()
    {
        currentId = default;
    }

    public string NextId
    {
        get
        {
            currentId = GenerateId(Interlocked.Increment(ref increment));
            return currentId;
        }
    }
    public readonly string CurrentId => currentId;

    /// <summary>
    /// This creates a net-new correlation
    /// </summary>
    /// <returns></returns>
    public static CorrelationId NewCorrelation()
    {
        return new CorrelationId();
    }
    public static CorrelationId NextCorrelationId()
    {
        throw new NotImplementedException();
    }

    private static string GenerateId(long id)
    {
        return string.Create(13, id, (buffer, value) =>
        {
            char[] encode32Chars = encoding.ToCharArray();

            buffer[12] = encode32Chars[value & 31];
            buffer[11] = encode32Chars[(value >> 5) & 31];
            buffer[10] = encode32Chars[(value >> 10) & 31];
            buffer[9] = encode32Chars[(value >> 15) & 31];
            buffer[8] = encode32Chars[(value >> 20) & 31];
            buffer[7] = encode32Chars[(value >> 25) & 31];
            buffer[6] = encode32Chars[(value >> 30) & 31];
            buffer[5] = encode32Chars[(value >> 35) & 31];
            buffer[4] = encode32Chars[(value >> 40) & 31];
            buffer[3] = encode32Chars[(value >> 45) & 31];
            buffer[2] = encode32Chars[(value >> 50) & 31];
            buffer[1] = encode32Chars[(value >> 55) & 31];
            buffer[0] = encode32Chars[(value >> 60) & 31];
        });
    }

    public bool Equals(CorrelationId correlationId)
    {
        return this.currentId == correlationId.currentId ? true : false;
    }
    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is CorrelationId correlationId ?
            this.Equals(correlationId) :
            false;
    }
    public override int GetHashCode() => HashCode.Combine(currentId);
    public override string ToString() => this.currentId;
}
