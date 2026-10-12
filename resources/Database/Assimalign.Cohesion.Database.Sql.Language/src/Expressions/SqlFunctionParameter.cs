namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A named SQL function parameter.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="DataType">The SQL type as declared.</param>
public sealed record SqlFunctionParameter(string Name, string DataType);
