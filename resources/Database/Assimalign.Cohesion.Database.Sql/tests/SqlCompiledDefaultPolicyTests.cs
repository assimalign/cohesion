using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCompiledDefaultPolicyTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Compiled expression DEFAULT retains the literal-only policy boundary (#1779, #1788)")]
    public async Task ApplySchemaAsync_CommonExpressionDefault_ShouldRefuseBeforePublication()
    {
        // Arrange: SQL DDL supports this expression; compiled declarations remain literal-only.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var declaration = SqlSchema.Compile("typed", schema => schema.Table<DefaultRow>("items", table =>
            table.DefaultSql(row => row.Amount, "COALESCE(NULL::INT,1.5)")));
        await using var engine = SqlDatabaseEngine.Create("compiled-default-policy", new());
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        long journal = database.CatalogStorage.JournalLength;

        // Act.
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(declaration, guard.Token));

        // Assert: supporting rollback of this declaration needs a separate policy decision.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03105");
        failure.Message.ShouldContain("Only literal DEFAULT values are supported", Case.Sensitive);
        database.Provisioning.ShouldBeNull();
        database.CatalogStorage.JournalLength.ShouldBe(journal);
        database.Catalog.Tables.ShouldBeEmpty();
        database.Catalog.AppliedSchemaHistory.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A foreign schema name reports the planning refusal through Provisioning (#1779)")]
    public async Task ApplySchemaAsync_ForeignSchemaName_ShouldReportProvisioning()
    {
        // Arrange
        var declaration = SqlSchema.Compile("another", schema => schema.Table<DefaultRow>("items", table => table.Column(row => row.Amount)));
        await using var engine = SqlDatabaseEngine.Create("schema-name-policy", new());
        var database = await engine.CreateDatabaseAsync("typed", CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.ApplySchemaAsync(declaration, CancellationToken.None));

        // Assert: an uncoded planning refusal is re-coded and reported.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03105");
        database.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03105");
        database.Catalog.Tables.ShouldBeEmpty();
        database.Catalog.AppliedSchemaHistory.ShouldBeEmpty();
    }

    private sealed record DefaultRow(decimal? Amount);

    [Fact(DisplayName = "Cohesion Test [Sql] - Compiled expression DEFAULT keeps Build phase and script cause diagnostics (#1779, #1788)")]
    public async Task Generate_CommonExpressionDefault_ShouldPreservePolicyDiagnostic()
    {
        // Arrange
        var schema = SqlSchema.Create("typed", declaration => declaration.Table<DefaultRow>("items", table =>
            table.DefaultSql(row => row.Amount, "COALESCE(NULL::INT,1.5)")));
        var compiled = schema.Compile();
        var builder = SqlDatabaseEngine.CreateBuilder("compiled-default-build-policy");
        builder.AddDatabase(schema);

        // Act
        var build = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync(CancellationToken.None));
        var script = Should.Throw<SqlSchemaMigrationException>(() =>
            SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(null, compiled)));

        // Assert: Build names the phase; the cause retains the policy identity.
        build.Message.ShouldStartWith("CS03101:", Case.Sensitive);
        build.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03105");
        script.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03105");
        script.Message.ShouldContain("Only literal DEFAULT values are supported", Case.Sensitive);
    }
}
