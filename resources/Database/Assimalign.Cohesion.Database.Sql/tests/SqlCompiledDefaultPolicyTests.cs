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
        database.CatalogStorage.JournalLength.ShouldBe(journal);
        database.Catalog.Tables.ShouldBeEmpty();
        database.Catalog.AppliedSchemaHistory.ShouldBeEmpty();
    }

    private sealed record DefaultRow(decimal? Amount);
}
