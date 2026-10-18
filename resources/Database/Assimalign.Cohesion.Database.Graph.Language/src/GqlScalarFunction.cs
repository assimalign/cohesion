namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The executable scalar function identities; aliases resolve to the same identity.</summary>
public enum GqlScalarFunction
{
    /// <summary>The element_id function.</summary>
    ElementId,
    /// <summary>The labels function.</summary>
    Labels,
    /// <summary>The type function.</summary>
    Type,
    /// <summary>The properties function.</summary>
    Properties,
    /// <summary>The keys function.</summary>
    Keys,
    /// <summary>The startNode function.</summary>
    StartNode,
    /// <summary>The endNode function.</summary>
    EndNode,
    /// <summary>The path_length function.</summary>
    PathLength,
    /// <summary>The nodes function.</summary>
    Nodes,
    /// <summary>The edges function.</summary>
    Relationships,
    /// <summary>The lower function.</summary>
    Lower,
    /// <summary>The upper function.</summary>
    Upper,
    /// <summary>The trim function.</summary>
    Trim,
    /// <summary>The substring function.</summary>
    Substring,
    /// <summary>The replace function.</summary>
    Replace,
    /// <summary>The char_length function.</summary>
    CharLength,
    /// <summary>The size function.</summary>
    Size,
    /// <summary>The abs function.</summary>
    Abs,
    /// <summary>The ceil function.</summary>
    Ceil,
    /// <summary>The floor function.</summary>
    Floor,
    /// <summary>The round function.</summary>
    Round,
    /// <summary>The sqrt function.</summary>
    Sqrt,
    /// <summary>The log function.</summary>
    Log,
    /// <summary>The mod function.</summary>
    Mod,
    /// <summary>The toString function.</summary>
    ToString,
    /// <summary>The toInteger function.</summary>
    ToInteger,
    /// <summary>The toFloat function.</summary>
    ToFloat,
    /// <summary>The toBoolean function.</summary>
    ToBoolean,
    /// <summary>The current_date function.</summary>
    CurrentDate,
    /// <summary>The current_timestamp function.</summary>
    CurrentTimestamp,
}
