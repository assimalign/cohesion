using System;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Resolved options: a future SQL binder owns duplicate-option and NO MIN/MAX syntax.
internal sealed class SqlSequenceOptions
{
    public SqlSequenceOptions(DatabaseType type = DatabaseType.Int64, long? start = null, long increment = 1,
        long? minimum = null, long? maximum = null, long cache = 1, bool cycle = false)
    {
        // PostgreSQL src/backend/commands/sequence.c:1415-1587 (init_params).
        var (typeMinimum, typeMaximum) = IntegerBounds(type);
        if (increment == 0) { throw new SqlCatalogException("CS03069", ErrorMessages.SequenceIncrementZero); }
        long min = minimum ?? (increment > 0 ? 1 : typeMinimum);
        long max = maximum ?? (increment > 0 ? typeMaximum : -1);
        if (min < typeMinimum || min > typeMaximum || max < typeMinimum || max > typeMaximum || min >= max)
        { throw new SqlCatalogException("CS03069", ErrorMessages.SequenceBoundsInvalid); }
        long first = start ?? (increment > 0 ? min : max);
        if (first < min || first > max) { throw new SqlCatalogException("CS03069", ErrorMessages.SequenceStartInvalid); }
        if (cache < 1) { throw new SqlCatalogException("CS03069", ErrorMessages.SequenceCacheInvalid); }
        Type = type;
        Start = first;
        Increment = increment;
        Minimum = min;
        Maximum = max;
        Cache = cache;
        Cycle = cycle;
    }

    public DatabaseType Type { get; }
    public long Start { get; }
    public long Increment { get; }
    public long Minimum { get; }
    public long Maximum { get; }
    public long Cache { get; }
    public bool Cycle { get; }

    public void ValidateRestart(long value)
    {
        if (value < Minimum || value > Maximum) { throw new SqlCatalogException("CS03069", ErrorMessages.SequenceRestartInvalid); }
    }

    internal static (long Minimum, long Maximum) IntegerBounds(DatabaseType type, bool identity = false)
        => type switch
        {
            DatabaseType.Int8 when identity => (sbyte.MinValue, sbyte.MaxValue),
            DatabaseType.Int16 => (short.MinValue, short.MaxValue),
            DatabaseType.Int32 => (int.MinValue, int.MaxValue),
            DatabaseType.Int64 => (long.MinValue, long.MaxValue),
            _ => throw new SqlCatalogException("CS03069", ErrorMessages.SequenceTypeInvalid),
        };
}
