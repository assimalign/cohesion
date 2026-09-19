using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Mapping.AotGuard;

internal static class GuardSchema
{
    // This retained C# declaration is the generator input. Deployment below consumes its
    // generated compiled tables, so the NativeAOT process never inspects expression members.
    internal static SqlCompiledSchema Declare()
        => SqlSchema.Compile("mapping_guard", database =>
        {
            database.Table<GuardParent>("guard_parents", table =>
            {
                table.Key(parent => parent.Id);
                table.Column(parent => parent.Name);
            });
            database.Table<GuardChild>("guard_children", table =>
            {
                table.Key(child => child.Id);
                table.References<GuardParent>(child => child.ParentId);
                table.Column(child => child.Name);
                table.Column(child => child.Payload);
            });
        });

    internal static SqlCompiledSchema Deployment()
        => new(SqlCompiledSchema.CurrentFormat, "mapping_guard", EngineModel.Sql, false, [],
            [GuardParentMapper.SchemaTable, GuardChildMapper.SchemaTable], [], [], [], []);
}
