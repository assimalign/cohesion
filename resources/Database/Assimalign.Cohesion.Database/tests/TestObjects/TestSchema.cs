namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A compiled schema the base suite applies.
/// </summary>
internal sealed class TestSchema : CompiledSchema
{
    public TestSchema(string name)
        : base("tests/schema/v1", name, EngineModel.Sql, allowsDestructiveChanges: false)
    {
    }

    public override string CanonicalDocument => "{\"tables\":[]}";
}
