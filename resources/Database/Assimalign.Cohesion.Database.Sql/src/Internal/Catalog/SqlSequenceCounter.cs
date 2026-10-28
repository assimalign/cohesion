namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed class SqlSequenceCounter : SqlAllocationCounter
{
    public long Reserved { get; set; }
    public bool IsCalled { get; set; }
}
