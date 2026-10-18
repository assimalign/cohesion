using System;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[Flags]
internal enum GraphFunctionKind
{
    Unknown = 0, Node = 1, Relationship = 2, Path = 4, String = 8, Integral = 16,
    Boolean = 32, List = 64, Map = 128, Date = 256, Timestamp = 512,
    NonIntegral = 1024, Number = Integral | NonIntegral,
    Scalar = String | Number | Boolean | Date | Timestamp,
}

/// <summary>Static signatures and result shapes; no reflection or runtime discovery.</summary>
internal static class GraphScalarFunctions
{
    internal static string Name(GqlScalarFunction function) => function switch
    {
        GqlScalarFunction.ElementId => "element_id", GqlScalarFunction.PathLength => "path_length",
        GqlScalarFunction.CharLength => "char_length", GqlScalarFunction.CurrentDate => "CURRENT_DATE",
        GqlScalarFunction.CurrentTimestamp => "CURRENT_TIMESTAMP",
        GqlScalarFunction.Relationships => "edges",
        _ => function.ToString(),
    };

    internal static bool ValidArity(GqlFunctionCallExpression call) => Enum.IsDefined(call.Function) && call.Arguments is not null && call.Function switch
    {
        GqlScalarFunction.CurrentDate or GqlScalarFunction.CurrentTimestamp => call.Arguments.Count == 0,
        GqlScalarFunction.Substring => call.Arguments.Count is 2 or 3,
        GqlScalarFunction.Round or GqlScalarFunction.Log => call.Arguments.Count is 1 or 2,
        GqlScalarFunction.Replace => call.Arguments.Count == 3,
        GqlScalarFunction.Mod => call.Arguments.Count == 2,
        _ => call.Arguments.Count == 1,
    };

    internal static GraphFunctionKind Argument(GqlScalarFunction function, int index) => function switch
    {
        GqlScalarFunction.ElementId => GraphFunctionKind.Node | GraphFunctionKind.Relationship,
        GqlScalarFunction.Labels => GraphFunctionKind.Node,
        GqlScalarFunction.Type or GqlScalarFunction.StartNode or GqlScalarFunction.EndNode => GraphFunctionKind.Relationship,
        GqlScalarFunction.Properties or GqlScalarFunction.Keys => GraphFunctionKind.Node | GraphFunctionKind.Relationship | GraphFunctionKind.Map,
        GqlScalarFunction.PathLength or GqlScalarFunction.Nodes or GqlScalarFunction.Relationships => GraphFunctionKind.Path,
        GqlScalarFunction.Lower or GqlScalarFunction.Upper or GqlScalarFunction.Trim or GqlScalarFunction.CharLength or GqlScalarFunction.Replace => GraphFunctionKind.String,
        GqlScalarFunction.Substring => index == 0 ? GraphFunctionKind.String : GraphFunctionKind.Number,
        GqlScalarFunction.Size => GraphFunctionKind.List | GraphFunctionKind.String,
        GqlScalarFunction.ToInteger => GraphFunctionKind.Number | GraphFunctionKind.String | GraphFunctionKind.Boolean,
        GqlScalarFunction.ToFloat => GraphFunctionKind.Number | GraphFunctionKind.String,
        GqlScalarFunction.ToBoolean => GraphFunctionKind.Integral | GraphFunctionKind.String | GraphFunctionKind.Boolean,
        GqlScalarFunction.ToString => GraphFunctionKind.Scalar,
        _ => GraphFunctionKind.Number,
    };

    internal static GraphFunctionKind Result(GqlScalarFunction function) => function switch
    {
        GqlScalarFunction.StartNode or GqlScalarFunction.EndNode => GraphFunctionKind.Node,
        GqlScalarFunction.Labels or GqlScalarFunction.Keys or GqlScalarFunction.Nodes or GqlScalarFunction.Relationships => GraphFunctionKind.List,
        GqlScalarFunction.Properties => GraphFunctionKind.Map,
        GqlScalarFunction.ElementId or GqlScalarFunction.Type or GqlScalarFunction.Lower or GqlScalarFunction.Upper or
            GqlScalarFunction.Trim or GqlScalarFunction.Substring or GqlScalarFunction.Replace or GqlScalarFunction.ToString => GraphFunctionKind.String,
        GqlScalarFunction.ToBoolean => GraphFunctionKind.Boolean,
        GqlScalarFunction.CurrentDate => GraphFunctionKind.Date,
        GqlScalarFunction.CurrentTimestamp => GraphFunctionKind.Timestamp,
        GqlScalarFunction.PathLength or GqlScalarFunction.CharLength or GqlScalarFunction.Size or GqlScalarFunction.ToInteger => GraphFunctionKind.Integral,
        GqlScalarFunction.ToFloat or GqlScalarFunction.Sqrt or GqlScalarFunction.Log => GraphFunctionKind.NonIntegral,
        _ => GraphFunctionKind.Number,
    };

    internal static GraphFunctionKind Kind(object? value) => value switch
    {
        null => GraphFunctionKind.Unknown,
        GraphNode => GraphFunctionKind.Node, GraphRelationship => GraphFunctionKind.Relationship, GraphPath => GraphFunctionKind.Path,
        string => GraphFunctionKind.String, bool => GraphFunctionKind.Boolean, System.Collections.IList when value is not byte[] => GraphFunctionKind.List,
        System.Collections.Generic.IReadOnlyDictionary<string, object?> => GraphFunctionKind.Map,
        DateOnly => GraphFunctionKind.Date, DateTimeOffset => GraphFunctionKind.Timestamp,
        byte or sbyte or short or ushort or int or uint or long or ulong => GraphFunctionKind.Integral,
        _ => GraphValueComparer.IsNumber(value) ? GraphFunctionKind.NonIntegral : GraphFunctionKind.Unknown,
    };

    internal static GraphProjectionKind Entity(GqlScalarFunction function, int depth) => (function, depth) switch
    {
        (GqlScalarFunction.StartNode or GqlScalarFunction.EndNode, 0) or (GqlScalarFunction.Nodes, 1) => GraphProjectionKind.Node,
        (GqlScalarFunction.Relationships, 1) => GraphProjectionKind.Relationship,
        _ => GraphProjectionKind.Scalar,
    };

    internal static bool Composite(GqlScalarFunction function, int depth) => depth == 0 && Result(function) is GraphFunctionKind.List or GraphFunctionKind.Map;

    internal static DatabaseType Type(GqlScalarFunction function) => function switch
    {
        GqlScalarFunction.CurrentDate => DatabaseType.Date,
        GqlScalarFunction.CurrentTimestamp => DatabaseType.DateTimeOffset,
        GqlScalarFunction.ElementId or GqlScalarFunction.Type or GqlScalarFunction.Lower or GqlScalarFunction.Upper or
            GqlScalarFunction.Trim or GqlScalarFunction.Substring or GqlScalarFunction.Replace or GqlScalarFunction.ToString => DatabaseType.String,
        GqlScalarFunction.PathLength or GqlScalarFunction.CharLength or GqlScalarFunction.Size or GqlScalarFunction.ToInteger => DatabaseType.Int64,
        GqlScalarFunction.ToBoolean => DatabaseType.Boolean,
        GqlScalarFunction.ToFloat or GqlScalarFunction.Sqrt or GqlScalarFunction.Log => DatabaseType.Float64,
        _ => DatabaseType.Null,
    };

    internal static string Name(GqlCastType target) => target switch
    {
        GqlCastType.Int32 => "INTEGER", GqlCastType.Int64 => "BIGINT",
        GqlCastType.Float32 => "REAL", GqlCastType.Float64 => "DOUBLE",
        GqlCastType.Decimal => "DECIMAL", GqlCastType.String => "STRING",
        GqlCastType.Boolean => "BOOLEAN", GqlCastType.Date => "DATE", _ => "ZONED DATETIME",
    };

    internal static DatabaseType Type(GqlCastType target) => target switch
    {
        GqlCastType.Int32 => DatabaseType.Int32, GqlCastType.Int64 => DatabaseType.Int64,
        GqlCastType.Float32 => DatabaseType.Float32, GqlCastType.Float64 => DatabaseType.Float64,
        GqlCastType.Decimal => DatabaseType.Decimal, GqlCastType.String => DatabaseType.String,
        GqlCastType.Boolean => DatabaseType.Boolean, GqlCastType.Date => DatabaseType.Date,
        _ => DatabaseType.DateTimeOffset,
    };
}
