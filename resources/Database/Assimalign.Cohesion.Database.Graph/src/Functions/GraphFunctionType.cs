using System;
using System.Collections;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Immutable graph query-value metadata, including nested list/map element shape.</summary>
[System.Diagnostics.DebuggerDisplay("GraphFunctionType {Kind}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphFunctionType.DebugView))]
public sealed class GraphFunctionType
{
    private GraphFunctionType(GraphFunctionKind kind, DatabaseType databaseType = DatabaseType.Null, Type? clrType = null,
        GraphFunctionType? element = null, bool nullable = true)
    { Kind = kind; DatabaseType = databaseType; ClrType = clrType; Element = element; IsNullable = nullable; }
    internal GraphFunctionKind Kind { get; }
    internal DatabaseType DatabaseType { get; }
    internal Type? ClrType { get; }
    /// <summary>Gets the declared query-value category.</summary>
    public GraphFunctionValueKind ValueKind => Kind switch
    {
        GraphFunctionKind.Node => GraphFunctionValueKind.Node,
        GraphFunctionKind.Relationship => GraphFunctionValueKind.Relationship,
        GraphFunctionKind.Path => GraphFunctionValueKind.Path,
        GraphFunctionKind.List => GraphFunctionValueKind.List,
        GraphFunctionKind.Map => GraphFunctionValueKind.Map,
        GraphFunctionKind.String => GraphFunctionValueKind.String,
        GraphFunctionKind.Boolean => GraphFunctionValueKind.Boolean,
        GraphFunctionKind.Date => GraphFunctionValueKind.Date,
        GraphFunctionKind.Timestamp => GraphFunctionValueKind.Timestamp,
        GraphFunctionKind.Integral => GraphFunctionValueKind.Integral,
        GraphFunctionKind.NonIntegral => GraphFunctionValueKind.NonIntegral,
        GraphFunctionKind.Number => GraphFunctionValueKind.Number,
        GraphFunctionKind.Scalar => GraphFunctionValueKind.Scalar,
        _ => GraphFunctionValueKind.Any,
    };
    /// <summary>Gets the exact scalar column type, or Null for a union/entity/composite.</summary>
    public DatabaseType ScalarType => DatabaseType;
    internal static GraphFunctionType Native(GraphFunctionKind kind, DatabaseType scalarType = DatabaseType.Null) => new(kind, scalarType);
    /// <summary>Gets a nested list element or map value shape, or null for a leaf.</summary>
    public GraphFunctionType? Element { get; }
    /// <summary>Gets whether this value may be NULL.</summary>
    public bool IsNullable { get; }
    /// <summary>Any supported query value; conservatively includes entities and composites.</summary>
    public static GraphFunctionType Any { get; } = new(GraphFunctionKind.Scalar | GraphFunctionKind.Node | GraphFunctionKind.Relationship | GraphFunctionKind.Path | GraphFunctionKind.List | GraphFunctionKind.Map);
    /// <summary>Any supported numeric value.</summary>
    public static GraphFunctionType Number { get; } = new(GraphFunctionKind.Number);
    /// <summary>A graph node.</summary>
    public static GraphFunctionType Node { get; } = new(GraphFunctionKind.Node, clrType: typeof(GraphNode));
    /// <summary>A graph relationship.</summary>
    public static GraphFunctionType Relationship { get; } = new(GraphFunctionKind.Relationship, clrType: typeof(GraphRelationship));
    /// <summary>A graph path.</summary>
    public static GraphFunctionType Path { get; } = new(GraphFunctionKind.Path, clrType: typeof(GraphPath));
    /// <summary>A query string.</summary>
    public static GraphFunctionType String { get; } = Of<string>();
    /// <summary>A signed 64-bit integer.</summary>
    public static GraphFunctionType Int64 { get; } = Of<long>();
    /// <summary>A double precision floating point number.</summary>
    public static GraphFunctionType Float64 { get; } = Of<double>();
    /// <summary>A Boolean.</summary>
    public static GraphFunctionType Boolean { get; } = Of<bool>();
    /// <summary>A list with the given immutable element metadata.</summary>
    /// <param name="element">The element shape.</param><returns>The list shape.</returns>
    /// <exception cref="ArgumentNullException">The element shape is null.</exception>
    /// <exception cref="ArgumentException">Nesting exceeds the graph value codec depth.</exception>
    public static GraphFunctionType List(GraphFunctionType element) { ArgumentNullException.ThrowIfNull(element); ValidateDepth(element); return new(GraphFunctionKind.List, element: element); }
    /// <summary>A string-keyed map with the given immutable value metadata.</summary>
    /// <param name="value">The value shape.</param><returns>The map shape.</returns>
    /// <exception cref="ArgumentNullException">The value shape is null.</exception>
    /// <exception cref="ArgumentException">Nesting exceeds the graph value codec depth.</exception>
    public static GraphFunctionType Map(GraphFunctionType value) { ArgumentNullException.ThrowIfNull(value); ValidateDepth(value); return new(GraphFunctionKind.Map, element: value); }
    /// <summary>Creates this shape with a non-NULL contract.</summary><returns>The nonnullable shape.</returns>
    public GraphFunctionType NotNull() => new(Kind, DatabaseType, ClrType, Element, false);
    /// <summary>Gets metadata for a supported CLR query type without runtime code generation.</summary>
    /// <typeparam name="T">A canonical query type (numeric: Int64, Double or Decimal, including nullable forms), or object for any query value.</typeparam><returns>The metadata.</returns>
    /// <exception cref="ArgumentException">The CLR type is unsupported.</exception>
    public static GraphFunctionType Of<T>()
    {
        Type type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (type == typeof(object)) { return Any; }
        if (type == typeof(GraphNode)) { return Node; }
        if (type == typeof(GraphRelationship)) { return Relationship; }
        if (type == typeof(GraphPath)) { return Path; }
        if (type == typeof(object[])) { return List(Any); }
        if (type == typeof(IReadOnlyDictionary<string, object?>)) { return Map(Any); }
        (GraphFunctionKind kind, DatabaseType databaseType) = type == typeof(string) ? (GraphFunctionKind.String, DatabaseType.String)
            : type == typeof(bool) ? (GraphFunctionKind.Boolean, DatabaseType.Boolean)
            : type == typeof(long) ? (GraphFunctionKind.Integral, DatabaseType.Int64)
            : type == typeof(double) ? (GraphFunctionKind.NonIntegral, DatabaseType.Float64)
            : type == typeof(decimal) ? (GraphFunctionKind.NonIntegral, DatabaseType.Decimal)
            : type == typeof(DateOnly) ? (GraphFunctionKind.Date, DatabaseType.Date)
            : type == typeof(DateTimeOffset) ? (GraphFunctionKind.Timestamp, DatabaseType.DateTimeOffset)
            : throw new ArgumentException(ErrorMessages.FunctionTypeUnsupported);
        return new(kind, databaseType, type);
    }
    internal static GraphFunctionType Parameter<T>(GraphNullBehavior nullBehavior)
    {
        if (nullBehavior == GraphNullBehavior.CalledOnNullInput && default(T) is not null)
        { throw new ArgumentException(ErrorMessages.FunctionNullParameterInvalid); }
        return Of<T>();
    }
    private static void ValidateDepth(GraphFunctionType shape)
    {
        int depth = 0;
        for (GraphFunctionType? current = shape; current is not null; current = current.Element)
        { if (++depth > GraphValueCodec.MaxDepth) { throw new ArgumentException(ErrorMessages.FunctionTypeUnsupported); } }
    }
    internal GraphFunctionType AsNullable() => IsNullable ? this : new(Kind, DatabaseType, ClrType, Element, true);
    internal GraphFunctionType? At(int depth)
    {
        GraphFunctionType? shape = this;
        while (depth-- > 0 && shape is not null)
        {
            // Any includes both entities and containers. Access may still yield an arbitrary
            // nested container; the entity-only property shortcut cannot narrow this union.
            if ((shape.Kind & (GraphFunctionKind.List | GraphFunctionKind.Map)) != 0 && shape.Element is null) { shape = Any; continue; }
            if ((shape.Kind & (GraphFunctionKind.Node | GraphFunctionKind.Relationship)) != 0) { return depth == 0 ? new(GraphFunctionKind.Scalar) : null; }
            shape = shape.Element ?? ((shape.Kind & (GraphFunctionKind.List | GraphFunctionKind.Map)) != 0 ? Any : null);
        }
        return shape;
    }
    internal bool Accepts(object? value)
    {
        if (value is null) { return IsNullable; }
        if ((Kind & GraphScalarFunctions.Kind(value)) == 0 || ClrType is not null && value.GetType() != ClrType) { return false; }
        if (Element is not null)
        {
            if (value is IReadOnlyDictionary<string, object?> map)
            { foreach (var child in map.Values) { if (!Element.Accepts(child)) { return false; } } }
            else if (value is IList list)
            { foreach (var child in list) { if (!Element.Accepts(child)) { return false; } } }
        }
        return true;
    }
    private sealed class DebugView
    {
        private readonly GraphFunctionType _target;
        public DebugView(GraphFunctionType value) { _target = value; }
        public bool IsNullable => _target.IsNullable;
        public GraphFunctionType? Element => _target.Element;
    }
}
