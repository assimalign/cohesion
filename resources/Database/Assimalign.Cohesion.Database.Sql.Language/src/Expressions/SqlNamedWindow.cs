namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A WINDOW name AS (specification) definition.</summary>
public sealed class SqlNamedWindow
{
    internal SqlNamedWindow(string name, SqlWindowSpecification specification)
    {
        Name = name;
        Specification = specification;
    }
    /// <summary>Gets the window's name.</summary>
    public string Name { get; }
    /// <summary>Gets its specification.</summary>
    public SqlWindowSpecification Specification { get; }
}
