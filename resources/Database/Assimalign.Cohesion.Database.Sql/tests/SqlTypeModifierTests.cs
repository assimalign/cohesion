using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTypeModifierTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Type modifiers: native decimal descriptors use the SQL declaration rule")]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(28, 28, true)]
    [InlineData(29, 0, false)]
    [InlineData(29, 29, false)]
    [InlineData(40, 2, false)]
    [InlineData(40, 30, false)]
    [InlineData(28, -1, false)]
    public void Decimal_ModifierBoundary_ShouldValidate(int precision, int scale, bool valid)
    {
        // Arrange / Act / Assert
        if (valid)
        {
            SqlType.Decimal(precision, scale).Storage.Precision.ShouldBe(precision);
        }
        else
        {
            Should.Throw<ArgumentOutOfRangeException>(() => SqlType.Decimal(precision, scale));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Type modifiers: native string descriptors use the SQL length rule")]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10485760, true)]
    [InlineData(10485761, false)]
    public void VarChar_ModifierBoundary_ShouldValidate(int length, bool valid)
    {
        // Arrange / Act / Assert
        if (valid)
        {
            SqlType.VarChar(length).Storage.MaxLength.ShouldBe(length);
        }
        else
        {
            Should.Throw<ArgumentOutOfRangeException>(() => SqlType.VarChar(length));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Type modifiers: declarations and CAST accept identical boundaries before any write")]
    [MemberData(nameof(SqlTypeModifierCases.Sql), MemberType = typeof(SqlTypeModifierCases))]
    public async Task ExecuteAsync_ModifierBoundary_ShouldAgreeAtEverySite(string type, bool valid, string storage)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("modifiers", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE added (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE altered (value {(valid ? type : storage)})", cancellationToken: CancellationToken.None);

        foreach (var (sql, subject) in SqlTypeModifierCases.Statements(type))
        {
            // Act / Assert
            if (valid)
            {
                var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
                if (result is QueryResultSet rows) { await rows.DisposeAsync(); }
            }
            else
            {
                var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
                    await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
                error.Message.ShouldContain("SQL0005", Case.Sensitive);
                error.Message.ShouldContain(subject, Case.Sensitive);
            }
        }

        // Assert: rejection precedes catalog mutation and the session remains usable.
        database.Catalog.TryGetTable("dbo", "created", out _).ShouldBe(valid);
        database.Catalog.TryGetTable("dbo", "added", out var added).ShouldBeTrue();
        added.Columns.Count.ShouldBe(valid ? 2 : 1);
        await session.ExecuteAsync("INSERT INTO added (id) VALUES (1)", cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Type modifiers: scripts validate every statement before provisioning")]
    [MemberData(nameof(SqlTypeModifierCases.Sql), MemberType = typeof(SqlTypeModifierCases))]
    public async Task BuildAsync_ScriptModifierBoundary_ShouldValidateBeforeProvisioning(string type, bool valid, string storage)
    {
        // Arrange
        foreach (var (sql, subject) in SqlTypeModifierCases.Statements(type))
        {
            var builder = SqlDatabaseEngine.CreateBuilder("scripts");
            builder.AddDatabase("app", database => database.Script("001", $"CREATE TABLE added (id INT); CREATE TABLE altered (value {(valid ? type : storage)}); {sql}"));

            // Act / Assert
            if (valid)
            {
                await using var engine = await builder.BuildAsync(CancellationToken.None);
            }
            else
            {
                var error = await Should.ThrowAsync<DatabaseParseException>(async () => await builder.BuildAsync(CancellationToken.None));
                error.Message.ShouldContain("SQL0005", Case.Sensitive);
                error.Message.ShouldContain(subject, Case.Sensitive);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Type modifiers: typed columns use the same ranges at declaration and provisioning")]
    [MemberData(nameof(SqlTypeModifierCases.Columns), MemberType = typeof(SqlTypeModifierCases))]
    public async Task BuildAsync_TypedModifierBoundary_ShouldValidateAtDeclaration(string type, bool valid, string member)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("typed");

        // Act / Assert
        if (valid)
        {
            builder.AddDatabase("app", database => database.Schema(schema => schema.Table<SqlTypeModifierCases.Row>("typed", table =>
                table.Column(SqlTypeModifierCases.Selector(member), type))));
            await using var engine = await builder.BuildAsync(CancellationToken.None);
        }
        else
        {
            var error = Should.Throw<SqlSchemaValidationException>(() => builder.AddDatabase("app", database =>
                database.Schema(schema => schema.Table<SqlTypeModifierCases.Row>("typed", table =>
                    table.Column(SqlTypeModifierCases.Selector(member), type)))));
            error.Errors.ShouldHaveSingleItem().Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
            error.Message.ShouldContain($"typed.{member}", Case.Sensitive);
        }
    }
}
