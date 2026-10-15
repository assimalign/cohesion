using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public sealed class SqlTypeModifierTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Schema] - Temporal modifiers: typed and raw declarations report unsupported modifiers")]
    [MemberData(nameof(SqlTypeModifierCases.UnsupportedTemporal), MemberType = typeof(SqlTypeModifierCases))]
    public void Compile_TemporalModifiers_ShouldNameUnsupportedModifier(string type, string member)
    {
        // Arrange / Act
        var typed = Should.Throw<SqlSchemaValidationException>(() => SqlSchema.Create("app", schema =>
            schema.Table<SqlTypeModifierCases.Row>("items", table => table.Column(SqlTypeModifierCases.Selector(member), type))));
        var raw = Should.Throw<SqlSchemaValidationException>(() => new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", false, [],
            [new CompiledSchemaTable("items", "Row", [new CompiledSchemaColumn(member, DatabaseType.DateTime, true, CustomType: type)], null, [], [])], []));

        // Assert
        foreach (var (error, declaration) in new[] { (typed, $"items.{member}"), (raw, $"tables.items.columns.{member}.type") })
        {
            var diagnostic = error.Errors.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
            diagnostic.Declaration.ShouldBe(declaration);
            error.Message.ShouldContain("COHDBL001", Case.Sensitive);
            error.Message.ShouldContain(declaration, Case.Sensitive);
            error.Message.ShouldContain(type, Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Schema] - Raw decimal metadata: omitted scale has the explicit zero identity")]
    public void Compile_OmittedDecimalScale_ShouldNormalizeColumnsAndDomains()
    {
        // Arrange: a with expression also enters through the compiled-schema boundary.
        var column = new CompiledSchemaColumn("value", DatabaseType.Decimal, true) with { Precision = 3 };
        SqlCompiledSchema Compile(int? scale) => new(SqlCompiledSchema.CurrentFormat, "app", false,
            [new CompiledSchemaType("Money", DatabaseType.Decimal, 3, scale)],
            [new CompiledSchemaTable("items", "Row", [column with { Scale = scale }], null, [], [])], []);

        // Act
        var omitted = Compile(null);
        var explicitZero = Compile(0);

        // Assert
        omitted.Tables[0].Columns[0].Scale.ShouldBe(0);
        omitted.Types[0].Scale.ShouldBe(0);
        omitted.CanonicalDocument.ShouldBe(explicitZero.CanonicalDocument);
        omitted.Hash.ShouldBe(explicitZero.Hash);
        SqlCompiledSchemaSerializer.Deserialize(omitted.CanonicalDocument.Replace("\"scale\":0", "\"scale\":null"))
            .Hash.ShouldBe(explicitZero.Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Schema] - Type modifiers: typed declarations enforce every SQL boundary")]
    [MemberData(nameof(SqlTypeModifierCases.Columns), MemberType = typeof(SqlTypeModifierCases))]
    public void Create_ModifierBoundary_ShouldValidateAtDeclaration(string type, bool valid, string member)
    {
        // Arrange
        Action<SqlSchemaBuilder> declare = schema => schema.Table<SqlTypeModifierCases.Row>("items", table =>
            table.Column(SqlTypeModifierCases.Selector(member), type));

        // Act / Assert
        if (valid)
        {
            var compiled = SqlSchema.Create("app", declare).Compile();
            compiled.Tables[0].Columns[0].CustomType.ShouldBe(type);
        }
        else
        {
            var error = Should.Throw<SqlSchemaValidationException>(() => SqlSchema.Create("app", declare));
            var detail = error.Errors.ShouldHaveSingleItem();
            detail.Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
            detail.Declaration.ShouldBe($"items.{member}");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Schema] - Type modifiers: decimal domains validate precision and scale at declaration")]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(28, 0, true)]
    [InlineData(28, 28, true)]
    [InlineData(28, 29, false)]
    [InlineData(29, 0, false)]
    [InlineData(28, -1, false)]
    public void Create_DomainBoundary_ShouldNameDomain(int precision, int scale, bool valid)
    {
        // Arrange
        Action<SqlSchemaBuilder> declare = schema => schema.Type<Money>(type => type.Decimal(precision, scale));

        // Act / Assert
        if (valid)
        {
            SqlSchema.Create("app", declare).Compile().Types.ShouldHaveSingleItem().Precision.ShouldBe(precision);
        }
        else
        {
            var error = Should.Throw<SqlSchemaValidationException>(() => SqlSchema.Create("app", declare));
            error.Errors.ShouldHaveSingleItem().Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
            error.Message.ShouldContain(nameof(Money), Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Schema] - Type modifiers: compiled metadata validates bounds without SQL text")]
    [InlineData(DatabaseType.Decimal, null, 0, 0, false)]
    [InlineData(DatabaseType.Decimal, null, 1, 0, true)]
    [InlineData(DatabaseType.Decimal, null, 28, 28, true)]
    [InlineData(DatabaseType.Decimal, null, 29, 0, false)]
    [InlineData(DatabaseType.Decimal, null, 1, 2, false)]
    [InlineData(DatabaseType.Decimal, null, null, 0, false)]
    [InlineData(DatabaseType.Decimal, 1, 1, 0, false)]
    [InlineData(DatabaseType.String, 0, null, null, false)]
    [InlineData(DatabaseType.String, 1, null, null, true)]
    [InlineData(DatabaseType.String, 10485760, null, null, true)]
    [InlineData(DatabaseType.String, 10485761, null, null, false)]
    [InlineData(DatabaseType.Binary, 0, null, null, false)]
    [InlineData(DatabaseType.Binary, 1, null, null, true)]
    [InlineData(DatabaseType.Binary, 10485760, null, null, true)]
    [InlineData(DatabaseType.Binary, 10485761, null, null, false)]
    [InlineData(DatabaseType.Int32, 5, null, null, false)]
    public void Compile_RawMetadataBoundary_ShouldValidate(DatabaseType type, int? length, int? precision, int? scale, bool valid)
    {
        // Arrange
        var table = new CompiledSchemaTable("items", "Row",
            [new CompiledSchemaColumn("value", type, true, length, precision, scale)], null, [], []);
        SqlCompiledSchema Compile() => new(SqlCompiledSchema.CurrentFormat, "app", false, [], [table], []);

        // Act / Assert
        if (valid)
        {
            Compile().Tables.ShouldHaveSingleItem();
        }
        else
        {
            var error = Should.Throw<SqlSchemaValidationException>(() => Compile());
            error.Errors.ShouldHaveSingleItem().Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
            error.Message.ShouldContain("items", Case.Sensitive);
            error.Message.ShouldContain("value", Case.Sensitive);
        }
    }

    private readonly record struct Money(decimal Value);
}
