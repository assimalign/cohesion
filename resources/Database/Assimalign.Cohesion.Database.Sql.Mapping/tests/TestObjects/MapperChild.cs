namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

internal sealed class MapperChild
{
    public int Id { get; set; }

    public int ParentId { get; set; }

    public string? Name { get; set; }

    public string? Note { get; set; }
}
