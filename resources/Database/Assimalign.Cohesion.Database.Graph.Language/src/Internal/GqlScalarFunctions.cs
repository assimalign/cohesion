using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

internal static class GqlScalarFunctions
{
    internal static readonly IReadOnlyDictionary<string, (GqlScalarFunction Function, int Min, int Max)> Signatures =
        new Dictionary<string, (GqlScalarFunction, int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["element_id"] = (GqlScalarFunction.ElementId, 1, 1),
            ["elementId"] = (GqlScalarFunction.ElementId, 1, 1),
            ["labels"] = (GqlScalarFunction.Labels, 1, 1),
            ["type"] = (GqlScalarFunction.Type, 1, 1),
            ["properties"] = (GqlScalarFunction.Properties, 1, 1),
            ["keys"] = (GqlScalarFunction.Keys, 1, 1),
            ["startNode"] = (GqlScalarFunction.StartNode, 1, 1),
            ["endNode"] = (GqlScalarFunction.EndNode, 1, 1),
            ["path_length"] = (GqlScalarFunction.PathLength, 1, 1),
            ["length"] = (GqlScalarFunction.PathLength, 1, 1),
            ["nodes"] = (GqlScalarFunction.Nodes, 1, 1),
            ["edges"] = (GqlScalarFunction.Relationships, 1, 1),
            ["relationships"] = (GqlScalarFunction.Relationships, 1, 1),
            ["lower"] = (GqlScalarFunction.Lower, 1, 1),
            ["toLower"] = (GqlScalarFunction.Lower, 1, 1),
            ["upper"] = (GqlScalarFunction.Upper, 1, 1),
            ["toUpper"] = (GqlScalarFunction.Upper, 1, 1),
            ["trim"] = (GqlScalarFunction.Trim, 1, 1),
            ["substring"] = (GqlScalarFunction.Substring, 2, 3),
            ["replace"] = (GqlScalarFunction.Replace, 3, 3),
            ["char_length"] = (GqlScalarFunction.CharLength, 1, 1),
            ["character_length"] = (GqlScalarFunction.CharLength, 1, 1),
            ["size"] = (GqlScalarFunction.Size, 1, 1),
            ["abs"] = (GqlScalarFunction.Abs, 1, 1),
            ["ceil"] = (GqlScalarFunction.Ceil, 1, 1),
            ["ceiling"] = (GqlScalarFunction.Ceil, 1, 1),
            ["floor"] = (GqlScalarFunction.Floor, 1, 1),
            ["round"] = (GqlScalarFunction.Round, 1, 2),
            ["sqrt"] = (GqlScalarFunction.Sqrt, 1, 1),
            ["ln"] = (GqlScalarFunction.Log, 1, 1),
            ["log"] = (GqlScalarFunction.Log, 1, 2),
            ["mod"] = (GqlScalarFunction.Mod, 2, 2),
            ["toString"] = (GqlScalarFunction.ToString, 1, 1),
            ["toInteger"] = (GqlScalarFunction.ToInteger, 1, 1),
            ["toFloat"] = (GqlScalarFunction.ToFloat, 1, 1),
            ["toBoolean"] = (GqlScalarFunction.ToBoolean, 1, 1),
            ["current_date"] = (GqlScalarFunction.CurrentDate, 0, 0),
            ["current_timestamp"] = (GqlScalarFunction.CurrentTimestamp, 0, 0),
        };
}
