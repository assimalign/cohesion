namespace Assimalign.Cohesion.Database.Sql.Mapping.AotGuard;

internal sealed class GuardChild
{
    public long Id { get; set; }

    public int ParentId { get; set; }

    public string? Name { get; set; }

    public byte[]? Payload { get; set; }
}
