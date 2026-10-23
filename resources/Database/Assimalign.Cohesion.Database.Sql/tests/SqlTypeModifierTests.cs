using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTypeModifierTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Interval qualifier: a delimited domain name remains an identifier")]
    public async Task ExecuteAsync_DelimitedIntervalDomainName_ShouldPreserveIdentifierAtEverySite()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("quoted-types", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN \"INTERVAL DAY\" AS INT", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE added (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE altered (value \"INTERVAL DAY\")", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (var (sql, subject) in SqlTypeModifierCases.Statements("\"INTERVAL DAY\""))
        {
            if (subject == "Domain 'bounded'")
            {
                // A domain name resolves, but nested domains retain their existing refusal.
                var error = await Should.ThrowAsync<DatabaseException>(async () =>
                    await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
                error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLD001");
                error.Message.ShouldBe("Domain 'bounded' must have a built-in base type.");
                continue;
            }
            var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            if (result is DatabaseResultSet rows) { await rows.DisposeAsync(); }
        }
        database.Catalog.TryGetTable("dbo", "created", out var table).ShouldBeTrue();
        table.FindColumn("value").ShouldNotBeNull().DomainName.ShouldBe("INTERVAL DAY");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Temporal modifiers: every declaration and script refuses unsupported semantics")]
    [MemberData(nameof(SqlTypeModifierCases.UnsupportedTemporal), MemberType = typeof(SqlTypeModifierCases))]
    public async Task ExecuteAsync_TemporalModifiers_ShouldFailClosedAtEverySite(string type, string member)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("temporal", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE added (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE altered (value {type.Split(' ', '(')[0]})", cancellationToken: CancellationToken.None);

        foreach (var (sql, subject) in SqlTypeModifierCases.Statements(type).Append(($"DROP FUNCTION parameter_function({type})", "parameter type")))
        {
            // Act / Assert
            var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
                await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
            error.Message.ShouldContain("CK00001", Case.Sensitive);
            error.ShouldContainMessage(subject, Case.Sensitive);
            error.ShouldContainMessage(type, Case.Sensitive);
            var builder = SqlDatabaseEngine.CreateBuilder("script");
            builder.AddDatabase("app", declaration => declaration.Script("001", sql));
            var scriptError = await Should.ThrowAsync<DatabaseParseException>(async () => await builder.BuildAsync(CancellationToken.None));
            scriptError.Message.ShouldContain("CK00001", Case.Sensitive);
            scriptError.ShouldContainMessage(subject, Case.Sensitive);
            scriptError.ShouldContainMessage(type, Case.Sensitive);
        }

        // Assert: typed declaration is refused before Build, and failed DDL left no objects.
        var typedError = Should.Throw<SqlSchemaValidationException>(() => SqlSchema.Compile("app", schema =>
            schema.Table<SqlTypeModifierCases.Row>("typed", table => table.Column(SqlTypeModifierCases.Selector(member), type))));
        typedError.Message.ShouldContain("CK00001", Case.Sensitive);
        typedError.Message.ShouldContain($"typed.{member}", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "created", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "added", out var added).ShouldBeTrue();
        added.Columns.Count.ShouldBe(1);
        database.Catalog.Domains.ShouldBeEmpty();
        database.Catalog.Functions.ShouldBeEmpty();
        await session.ExecuteAsync("INSERT INTO added VALUES (1)", cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FLOAT precision: all declaration sites persist and execute the resolved type")]
    [MemberData(nameof(SqlTypeModifierCases.Floating), MemberType = typeof(SqlTypeModifierCases))]
    public async Task ExecuteAsync_FloatPrecision_ShouldResolveCatalogAndValues(string type, DatabaseType expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("float", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE added (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE altered (value {type})", cancellationToken: CancellationToken.None);

        // Act
        foreach (var (sql, _) in SqlTypeModifierCases.Statements(type))
        {
            var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            if (result is DatabaseResultSet rows) { await rows.DisposeAsync(); }
        }

        // Assert
        foreach (string name in new[] { "created", "added", "altered" })
        {
            database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
            var storage = table.FindColumn("value").ShouldNotBeNull().Type;
            storage.Type.ShouldBe(expected);
            storage.MaxLength.ShouldBeNull();
            storage.Precision.ShouldBeNull();
            storage.Scale.ShouldBeNull();
        }
        string resolved = expected == DatabaseType.Float32 ? "REAL" : "DOUBLE";
        database.Catalog.Domains.ShouldHaveSingleItem().BaseType.ShouldBe(resolved);
        database.Catalog.Functions.Single(function => function.Name == "parameter_function").ParameterTypes.ShouldBe([resolved]);
        database.Catalog.Functions.Single(function => function.Name == "result_function").ReturnType.ShouldBe(resolved);
        await session.ExecuteAsync("INSERT INTO added (id) VALUES (1)", cancellationToken: CancellationToken.None);
        await using var casts = (await session.ExecuteAsync($"SELECT CAST(1.25 AS {type}) FROM added",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in casts.GetRowsAsync(CancellationToken.None))
        {
            if (expected == DatabaseType.Float32) { row.GetValue(0).ShouldBeOfType<float>().ShouldBe(1.25f); }
            else { row.GetValue(0).ShouldBeOfType<double>().ShouldBe(1.25d); }
        }
        await session.ExecuteAsync($"DROP FUNCTION parameter_function({type})", cancellationToken: CancellationToken.None);
    }

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
                if (result is DatabaseResultSet rows) { await rows.DisposeAsync(); }
            }
            else
            {
                var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
                    await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
                error.Message.ShouldContain("SQL0005", Case.Sensitive);
                error.ShouldContainMessage(subject, Case.Sensitive);
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
                error.ShouldContainMessage(subject, Case.Sensitive);
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
