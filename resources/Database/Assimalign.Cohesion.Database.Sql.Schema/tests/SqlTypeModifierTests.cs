using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public sealed class SqlTypeModifierTests
{
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
