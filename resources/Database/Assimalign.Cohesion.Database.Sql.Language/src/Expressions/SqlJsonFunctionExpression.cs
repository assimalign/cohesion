using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a SQL/JSON path query with its result and behavior clauses.</summary>
[System.Diagnostics.DebuggerDisplay("SQL/JSON {Kind}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SqlJsonFunctionExpression : SqlExpression
{
    internal SqlJsonFunctionExpression(SqlJsonFunctionKind kind, SqlExpression context, SqlExpression path,
        string? returning, DatabaseTypeInfo? returningType, SqlJsonWrapper wrapper, bool omitQuotes,
        SqlJsonBehavior onEmpty, SqlExpression? emptyDefault, SqlJsonBehavior onError, SqlExpression? errorDefault,
        Location? location) : base(location)
    {
        Kind = kind; Context = context; Path = path; Returning = returning; ReturningType = returningType;
        Wrapper = wrapper; OmitQuotes = omitQuotes; OnEmpty = onEmpty; EmptyDefault = emptyDefault;
        OnError = onError; ErrorDefault = errorDefault;
        Depth = 1 + Math.Max(Math.Max(context.Depth, path.Depth), Math.Max(DepthOf(emptyDefault), DepthOf(errorDefault)));
    }

    /// <summary>Gets the query operation.</summary>
    public SqlJsonFunctionKind Kind { get; }
    /// <summary>Gets the JSON context expression.</summary>
    public SqlExpression Context { get; }
    /// <summary>Gets the constant or parameter path.</summary>
    public SqlExpression Path { get; }
    /// <summary>Gets the explicit RETURNING declaration, or null for the operation's default.</summary>
    public string? Returning { get; }
    /// <summary>Gets a resolved built-in RETURNING type, if specified.</summary>
    public DatabaseTypeInfo? ReturningType { get; }
    /// <summary>Gets the array wrapper policy.</summary>
    public SqlJsonWrapper Wrapper { get; }
    /// <summary>Gets whether scalar string quotes are omitted.</summary>
    public bool OmitQuotes { get; }
    /// <summary>Gets the empty sequence policy.</summary>
    public SqlJsonBehavior OnEmpty { get; }
    /// <summary>Gets the optional ON EMPTY default expression.</summary>
    public SqlExpression? EmptyDefault { get; }
    /// <summary>Gets the path or coercion error policy.</summary>
    public SqlJsonBehavior OnError { get; }
    /// <summary>Gets the optional ON ERROR default expression.</summary>
    public SqlExpression? ErrorDefault { get; }

    /// <summary>Enumerates the expression's operands in source order.</summary>
    /// <returns>The context, path and any default expressions.</returns>
    public IEnumerable<SqlExpression> GetOperands()
    {
        yield return Context; yield return Path;
        if (EmptyDefault is not null) { yield return EmptyDefault; }
        if (ErrorDefault is not null) { yield return ErrorDefault; }
    }

    private sealed class DebugView
    {
        private readonly SqlJsonFunctionExpression _value;
        public DebugView(SqlJsonFunctionExpression value) { _value = value; }
        public SqlJsonFunctionKind Kind => _value.Kind;
        public SqlExpression Context => _value.Context;
        public SqlExpression Path => _value.Path;
    }
}

/// <summary>Identifies a SQL/JSON query operation.</summary>
public enum SqlJsonFunctionKind
{
    /// <summary>Returns one SQL scalar.</summary>
    Value,
    /// <summary>Returns JSON items with an optional wrapper.</summary>
    Query,
    /// <summary>Tests whether the path selects an item.</summary>
    Exists,
}

/// <summary>Controls SQL/JSON array wrapping.</summary>
public enum SqlJsonWrapper
{
    /// <summary>Requires at most one result.</summary>
    Without,
    /// <summary>Wraps multiple results.</summary>
    Conditional,
    /// <summary>Wraps every nonempty sequence.</summary>
    Unconditional,
}

/// <summary>Controls SQL/JSON empty and error results.</summary>
public enum SqlJsonBehavior
{
    /// <summary>Returns SQL NULL.</summary>
    Null,
    /// <summary>Raises the error.</summary>
    Error,
    /// <summary>Evaluates the clause's default expression.</summary>
    Default,
    /// <summary>Returns an empty array.</summary>
    EmptyArray,
    /// <summary>Returns an empty object.</summary>
    EmptyObject,
    /// <summary>Returns TRUE.</summary>
    True,
    /// <summary>Returns FALSE.</summary>
    False,
    /// <summary>Returns SQL UNKNOWN (NULL).</summary>
    Unknown,
}
