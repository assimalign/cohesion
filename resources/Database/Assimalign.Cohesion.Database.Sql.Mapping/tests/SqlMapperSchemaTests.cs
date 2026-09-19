using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

/// <summary>Checks that schema generation preserves the complete retained declaration used by acceptance tests.</summary>
public sealed class SqlMapperSchemaTests
{
    /// <summary>Generated deployment metadata and the retained compiler produce identical canonical documents.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Schema: generated deployment tables equal the retained compiler")]
    public void SchemaTable_RetainedDeclarations_ShouldProduceIdenticalDeploymentSchema()
    {
        // Arrange.
        var retained = MapperSchema.Declare();
        var generated = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "mapping_tests", EngineModel.Sql,
            false, [],
            [MapperParentMapper.SchemaTable, MapperChildMapper.SchemaTable, MapperScalarMapper.SchemaTable,
             MapperQuotedEntityMapper.SchemaTable], [], [], [], []);

        // Act / Assert: compare all columns, storage types, nullability, keys, foreign keys and row type IDs.
        generated.CanonicalDocument.ShouldBe(retained.CanonicalDocument);
        generated.Hash.ShouldBe(retained.Hash);
    }
}
