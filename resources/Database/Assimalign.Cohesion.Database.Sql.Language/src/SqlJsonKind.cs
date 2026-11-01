namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Classifies the JSON value tested by an IS JSON predicate.</summary>
public enum SqlJsonKind
{
    /// <summary>Any JSON value, including JSON null.</summary>
    Value,

    /// <summary>A JSON array.</summary>
    Array,

    /// <summary>A JSON object.</summary>
    Object,

    /// <summary>A JSON string, number, Boolean or null.</summary>
    Scalar,
}
