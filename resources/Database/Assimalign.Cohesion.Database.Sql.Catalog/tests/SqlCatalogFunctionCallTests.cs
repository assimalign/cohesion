using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogFunctionCallTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Repeated function identities preserve a deep CHECK within one record")]
    public async Task CreateTableAsync_RepeatedFunctionCalls_ShouldFitAndReopen()
    {
        // Arrange: the SQL text fits the catalog's record limit; repeating its identity must not exhaust it.
        const int depth = 1200;
        string expression = string.Concat(Enumerable.Repeat("ABS(", depth)) + "qty" + new string(')', depth) + " < 100";
        string[] calls = Enumerable.Repeat("F:ABS(4)", depth).ToArray();
        var check = new SqlCatalogConstraint("ck", SqlCatalogConstraintKind.Check, ["qty"],
            checkExpression: expression, checkFunctionCalls: calls);
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "repeated-calls");
        var catalog = SqlCatalog.Open(storage);

        // Act
        await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("qty", new DatabaseTypeInfo(DatabaseType.Int32))],
            [], DatabaseObjectOwner.Adhoc, null, default, [check]);
        var reopened = SqlCatalog.Open(storage).Tables.ShouldHaveSingleItem();

        // Assert
        reopened.Constraints.ShouldHaveSingleItem().CheckExpression.ShouldBe(expression);
        reopened.Constraints[0].CheckFunctionCalls.ShouldBe(calls);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Interleaved function identity runs retain every DEFAULT and CHECK call in order")]
    public async Task Open_InterleavedFunctionCalls_ShouldRetainEveryVector()
    {
        // Arrange
        string[] firstCalls = ["F:F(4)", "F:G(4)", "F:F(4)", "F:G(4)"];
        string[] nextCalls = ["F:H(4)", "F:H(4)"];
        string[] checkCalls = ["F:P(4,4)", "F:F(4)", "F:F(4)", "F:G(4)", "F:G(4)"];
        SqlCatalogColumn[] columns =
        [
            new("a", new DatabaseTypeInfo(DatabaseType.Int32), defaultLiteral: "f(g(f(g(1))))", defaultFunctionCalls: firstCalls),
            new("b", new DatabaseTypeInfo(DatabaseType.Int32), defaultLiteral: "h(h(1))", defaultFunctionCalls: nextCalls),
            new("c", new DatabaseTypeInfo(DatabaseType.Int32))
        ];
        var check = new SqlCatalogConstraint("ck", SqlCatalogConstraintKind.Check, [],
            checkExpression: "p(f(f(1)), g(g(1)))", checkFunctionCalls: checkCalls);
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "interleaved-calls");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "t", columns, [], DatabaseObjectOwner.Adhoc, null, default, [check]);

        // Act
        var reopened = SqlCatalog.Open(storage).Tables.ShouldHaveSingleItem();

        // Assert
        reopened.Columns[0].DefaultFunctionCalls.ShouldBe(firstCalls);
        reopened.Columns[1].DefaultFunctionCalls.ShouldBe(nextCalls);
        reopened.Columns[2].DefaultFunctionCalls.ShouldBeEmpty();
        reopened.Constraints.ShouldHaveSingleItem().CheckFunctionCalls.ShouldBe(checkCalls);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Function call vectors cannot outgrow their canonical expression")]
    public async Task CreateTableAsync_ImpossibleFunctionCallCount_ShouldRefuseBeforePublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "invalid-calls");
        var catalog = SqlCatalog.Open(storage);
        var column = new SqlCatalogColumn("v", new DatabaseTypeInfo(DatabaseType.Int32), defaultLiteral: "1",
            defaultFunctionCalls: ["F:F(4)", "F:F(4)"]);

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateTableAsync("dbo", "t", [column]).AsTask());

        // Assert
        failure.Message.ShouldContain("function-call count");
        catalog.Tables.ShouldBeEmpty();
    }
}
