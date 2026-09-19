using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

internal static class MapperSchema
{
    internal static SqlCompiledSchema Declare()
        => SqlSchema.Compile("mapping_tests", database =>
        {
            database.Table<MapperParent>("mapper_parents", table =>
            {
                table.Key(parent => parent.Id);
                table.Column(parent => parent.Name);
            });
            database.Table<MapperChild>("mapper_children", table =>
            {
                table.Key(child => child.Id);
                table.References<MapperParent>(child => child.ParentId);
                table.Index(child => child.Name);
                table.Column(child => child.Note);
            });
            database.Table<MapperScalar>("mapper_scalars", table =>
            {
                table.Key(value => value.Id);
                table.Column(value => value.Boolean);
                table.Column(value => value.Byte);
                table.Column(value => value.Int8);
                table.Column(value => value.Int16);
                table.Column(value => value.Int32);
                table.Column(value => value.Int64);
                table.Column(value => value.Float32);
                table.Column(value => value.Float64);
                table.Column(value => value.Decimal);
                table.Column(value => value.Text);
                table.Column(value => value.Binary);
                table.Column(value => value.Date);
                table.Column(value => value.Time);
                table.Column(value => value.Timestamp);
                table.Column(value => value.Offset);
                table.Column(value => value.Duration);
                table.Column(value => value.Guid);
            });
            database.Table<MapperQuotedEntity>("quoted mapper values", table =>
            {
                table.Key(value => value.Id);
                table.Column(value => value.@select);
            });
        });
}
