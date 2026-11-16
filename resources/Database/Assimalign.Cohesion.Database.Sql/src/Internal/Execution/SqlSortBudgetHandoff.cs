using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Private control transfer out of LINQ: never crosses the SQL execution boundary.
internal sealed class SqlSortBudgetHandoff : Exception
{
    private static readonly string _message = ErrorMessages.SortBudgetHandoff;
    internal (object?[] Row, object?[] Keys)[]? Buffer { get; set; }
    internal int Offset { get; }
    internal int Count { get; }
    internal long ScratchBytes { get; }
    internal SqlExecutionException Failure { get; }
    internal SqlSortBudgetHandoff((object?[] Row, object?[] Keys)[] buffer, int offset, int count, long scratchBytes, SqlExecutionException failure)
        : base(_message)
    { Buffer = buffer; Offset = offset; Count = count; ScratchBytes = scratchBytes; Failure = failure; }
}
