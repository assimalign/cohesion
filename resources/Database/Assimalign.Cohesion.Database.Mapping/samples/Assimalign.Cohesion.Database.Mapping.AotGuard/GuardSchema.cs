using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Mapping.AotGuard;

internal static class GuardSchema
{
    // The generator reads this existing schema declaration at compile time. The executable
    // exercises the generated mapping path; it does not run the schema builder's expression
    // inspection, which is a separate, existing schema-authoring concern.
    internal static SqlCompiledSchema Declare()
        => SqlSchema.Compile("mapping_guard", database => database.Table<GuardEntity>("entities", table =>
        {
            table.Key(entity => entity.Id);
            table.Column(entity => entity.Name);
            table.Column(entity => entity.Payload);
        }));
}
