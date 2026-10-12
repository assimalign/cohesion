using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogScriptTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Identity encoding quotes each component and doubles embedded quotes")]
    public void ObjectIdentity_SeparatorAndQuoteComponents_ShouldKeepDistinctTuples()
    {
        // Arrange
        string[] expected = ["T:\"a\".\"b.c\"", "T:\"a.b\".\"c\"", "T:\"a\"\"\".\"b:c\"",
            "I:\"a\".\"b\".\"c.d\"", "I:\"a\".\"b.c\".\"d\"", "I:\"a\".\"b\"\"\".\"c:d\""];

        // Act
        string[] actual = [SqlObjectIdentity.Table("a", "b.c"), SqlObjectIdentity.Table("a.b", "c"), SqlObjectIdentity.Table("a\"", "b:c"),
            SqlObjectIdentity.Index("a", "b", "c.d"), SqlObjectIdentity.Index("a", "b.c", "d"), SqlObjectIdentity.Index("a", "b\"", "c:d")];

        // Assert
        actual.ShouldBe(expected);
        actual.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(actual.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Table receipt targets distinguish quoted separator components")]
    [InlineData("a", "b.c", "a.b", "c")]
    [InlineData("a\"", "b.c", "a\".b", "c")]
    public async Task CreateTableAsync_QuotedDotReceiptTarget_ShouldRefuseDifferentTuple(
        string expectedSchema,
        string expectedTable,
        string actualSchema,
        string actualTable)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-table-separators");
        var catalog = SqlCatalog.Open(storage);
        ulong generation = catalog.DdlGeneration;
        var publication = new SqlCatalogScriptPublication("001", new string('A', 64), 1, null, generation, null,
            SqlObjectIdentity.Table(expectedSchema, expectedTable));

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync(actualSchema, actualTable,
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], null, publication, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Tables.ShouldBeEmpty();
        catalog.Scripts.ShouldBeEmpty();
        catalog.DdlGeneration.ShouldBe(generation);
        var reopened = SqlCatalog.Open(storage);
        reopened.Tables.ShouldBeEmpty();
        reopened.Scripts.ShouldBeEmpty();
        reopened.DdlGeneration.ShouldBe(generation);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Index receipt targets distinguish quoted separator components")]
    [InlineData("a", "b.c", "ix", "a.b", "c", "ix")]
    [InlineData("a", "b", "c.d", "a", "b.c", "d")]
    [InlineData("a", "b\"", "c.d", "a", "b\".c", "d")]
    public async Task CreateIndexAsync_QuotedDotReceiptTarget_ShouldRefuseDifferentTuple(
        string expectedSchema,
        string expectedTable,
        string expectedIndex,
        string actualSchema,
        string actualTable,
        string actualIndex)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-index-separators");
        var catalog = SqlCatalog.Open(storage);
        var table = await catalog.CreateTableAsync(actualSchema, actualTable,
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None);
        ulong generation = catalog.DdlGeneration;
        var publication = new SqlCatalogScriptPublication("001", new string('A', 64), 1, null, generation, null,
            SqlObjectIdentity.Index(expectedSchema, expectedTable, expectedIndex));
        var index = new SqlCatalogIndex(table.ObjectId, actualIndex, ["id"], false);
        var registration = new BTreeIndexRegistration(table.ObjectId, new IndexDefinition(actualIndex), 42);

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () =>
            await catalog.CreateIndexAsync(index, [registration], publication, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.GetIndexes(table.ObjectId).ShouldBeEmpty();
        catalog.GetIndexRegistrations().ShouldBeEmpty();
        catalog.Scripts.ShouldBeEmpty();
        catalog.DdlGeneration.ShouldBe(generation);
        var reopened = SqlCatalog.Open(storage);
        reopened.GetIndexes(table.ObjectId).ShouldBeEmpty();
        reopened.GetIndexRegistrations().ShouldBeEmpty();
        reopened.Scripts.ShouldBeEmpty();
        reopened.DdlGeneration.ShouldBe(generation);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Receipt targets use catalog case-insensitive name rules")]
    public async Task PublicationMethods_CaseVariantNames_ShouldCompleteMatchingTargets()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-target-case");
        var catalog = SqlCatalog.Open(storage);
        string hash = new('A', 64);
        var tablePublication = new SqlCatalogScriptPublication("001", hash, 2, null, catalog.DdlGeneration, null, "T:\"A\".\"B.C\"");

        // Act
        var table = await catalog.CreateTableAsync("a", "b.c", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)],
            null, tablePublication, CancellationToken.None);
        var indexPublication = new SqlCatalogScriptPublication("001", hash, 2, catalog.Scripts.ShouldHaveSingleItem(),
            catalog.DdlGeneration, null, "I:\"A\".\"B.C\".\"D:E.F\"");
        var index = new SqlCatalogIndex(table.ObjectId, "d:e.f", ["id"], false);
        await catalog.CreateIndexAsync(index, [new BTreeIndexRegistration(table.ObjectId, new IndexDefinition(index.Name), 42)],
            indexPublication, CancellationToken.None);

        // Assert
        var reopened = SqlCatalog.Open(storage);
        reopened.TryGetTable("A", "B.C", out _).ShouldBeTrue();
        reopened.TryGetIndex(table.ObjectId, "D:E.F", out _).ShouldBeTrue();
        var receipt = reopened.Scripts.ShouldHaveSingleItem();
        receipt.CompletedStatements.ShouldBe(2);
        receipt.StatementObjects.ShouldBe(["T:\"a\".\"b.c\"", "I:\"a\".\"b.c\".\"d:e.f\""]);
        receipt.Objects.ShouldBe(receipt.StatementObjects);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Function drops retain the requested equivalent-domain spelling")]
    public async Task DropFunctionAsync_EquivalentDomainAlias_ShouldPersistRequestedSpelling()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-function-alias");
        var catalog = SqlCatalog.Open(storage);
        var function = new SqlCatalogFunction("identity", "\"IDENTITY\"(5)", ["p"], ["a"], "BIGINT", "IMMUTABLE", false, "p", [], DatabaseObjectOwner.Schema, "App");
        await catalog.CreateFunctionAsync(function, CancellationToken.None);
        var publication = new SqlCatalogScriptPublication("001", new string('A', 64), 1, null, catalog.DdlGeneration, null, "F:" + function.Signature);

        // Act
        var parameterTypes = new[] { "b" };
        await catalog.DropFunctionAsync(function.Signature, parameterTypes, publication, CancellationToken.None);
        parameterTypes[0] = "changed";

        // Assert
        var reopened = SqlCatalog.Open(storage).Scripts.ShouldHaveSingleItem();
        reopened.StatementObjects.ShouldBe(["F:" + function.Signature]);
        reopened.StatementParameterTypes.ShouldHaveSingleItem().ShouldBe(["b"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Function drop receipt rejects mismatched requested parameter counts")]
    [InlineData(0)]
    [InlineData(2)]
    public async Task DropFunctionAsync_MismatchedParameterCount_ShouldKeepDefinition(int count)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-function-count");
        var catalog = SqlCatalog.Open(storage);
        var function = new SqlCatalogFunction("identity", "\"IDENTITY\"(5)", ["p"], ["BIGINT"], "BIGINT", "IMMUTABLE", false, "p", [], DatabaseObjectOwner.Schema, "App");
        await catalog.CreateFunctionAsync(function, CancellationToken.None);
        ulong generation = catalog.DdlGeneration;
        var publication = new SqlCatalogScriptPublication("001", new string('A', 64), 1, null, generation, null, "F:" + function.Signature);

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.DropFunctionAsync(function.Signature,
            Enumerable.Repeat("BIGINT", count).ToArray(), publication, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Functions.ShouldHaveSingleItem().ShouldBe(function);
        catalog.Scripts.ShouldBeEmpty();
        catalog.DdlGeneration.ShouldBe(generation);
        SqlCatalog.Open(storage).Functions.ShouldHaveSingleItem().Signature.ShouldBe(function.Signature);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Function drop receipt rejects blank requested parameter types")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DropFunctionAsync_BlankParameterType_ShouldKeepDefinition(string? parameterType)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-function-type-name");
        var catalog = SqlCatalog.Open(storage);
        var function = new SqlCatalogFunction("identity", "\"IDENTITY\"(5)", ["p"], ["BIGINT"], "BIGINT", "IMMUTABLE", false, "p", [], DatabaseObjectOwner.Schema, "App");
        await catalog.CreateFunctionAsync(function, CancellationToken.None);
        ulong generation = catalog.DdlGeneration;
        var publication = new SqlCatalogScriptPublication("001", new string('A', 64), 1, null, generation, null, "F:" + function.Signature);

        // Act
        await Should.ThrowAsync<ArgumentException>(async () => await catalog.DropFunctionAsync(function.Signature,
            [parameterType!], publication, CancellationToken.None));

        // Assert
        catalog.Functions.ShouldHaveSingleItem().ShouldBe(function);
        catalog.Scripts.ShouldBeEmpty();
        catalog.DdlGeneration.ShouldBe(generation);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Receipt-bearing drops certify an absent target without changing DDL generation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropAsync_AbsentTargetPublication_ShouldCompleteWithoutDdl(bool index)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-absent-target");
        var catalog = SqlCatalog.Open(storage);
        var table = await catalog.CreateTableAsync("dbo", "existing", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None);
        ulong generation = catalog.DdlGeneration;
        string target = index ? "I:\"dbo\".\"existing\".\"missing\"" : "T:\"dbo\".\"missing\"";
        var previous = await catalog.BeginScriptAsync("001", new string('A', 64), 1, null, CancellationToken.None);
        var publication = new SqlCatalogScriptPublication("001", previous.ContentHash, 1, previous, generation, null, target);

        // Act
        if (index) { await catalog.DropIndexAsync(table.ObjectId, "missing", [], publication, CancellationToken.None); }
        else { await catalog.DropTableAsync("dbo", "missing", publication, CancellationToken.None); }

        // Assert
        catalog.DdlGeneration.ShouldBe(generation);
        catalog.Tables.ShouldHaveSingleItem().ShouldBe(table);
        var receipt = SqlCatalog.Open(storage).Scripts.ShouldHaveSingleItem();
        receipt.CompletedStatements.ShouldBe(1);
        receipt.Objects.ShouldBeEmpty();
        receipt.StatementObjects.ShouldBe([target]);
        receipt.Generation.ShouldBe(generation);
        receipt.StatementParameterTypes.ShouldHaveSingleItem().ShouldBeEmpty();
        if (index)
        {
            await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.DropIndexAsync(table.ObjectId, "missing", [], CancellationToken.None));
            var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.DropIndexAsync(table.ObjectId, "missing", [], publication, CancellationToken.None));
            failure.Message.ShouldContain("COHSQLS001");
        }
        else
        {
            await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.DropTableAsync("dbo", "missing", CancellationToken.None));
            var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.DropTableAsync("dbo", "missing", publication, CancellationToken.None));
            failure.Message.ShouldContain("COHSQLS001");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Function receipts retain the actual declared parameter types after drop")]
    public async Task FunctionPublications_GivenDomainParameters_ShouldPersistTheirNames()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-function-types");
        var catalog = SqlCatalog.Open(storage);
        var function = new SqlCatalogFunction("identity", "\"IDENTITY\"(5)", ["p"], ["cents"], "BIGINT", "IMMUTABLE", false, "p", [], DatabaseObjectOwner.Schema, "App");
        var initial = await catalog.BeginScriptAsync("001", new string('A', 64), 2, null, CancellationToken.None);
        var create = new SqlCatalogScriptPublication("001", initial.ContentHash, 2, initial, catalog.DdlGeneration, null, "F:" + function.Signature);

        // Act
        await catalog.CreateFunctionAsync(function, create, CancellationToken.None);
        var drop = new SqlCatalogScriptPublication("001", initial.ContentHash, 2, catalog.Scripts.ShouldHaveSingleItem(), catalog.DdlGeneration, null, "F:" + function.Signature);
        await catalog.DropFunctionAsync(function.Signature, function.ParameterTypes, drop, CancellationToken.None);

        // Assert
        using var iterator = storage.GetUnitIterator();
        JsonDocument? receipt = null;
        while (iterator.MoveNext())
        {
            var reader = new DatabaseKeyReader(iterator.Current.Data.Span);
            if (reader.ReadInt32() == 12) { receipt = JsonDocument.Parse(reader.ReadString(out _)); }
        }
        using (receipt)
        {
            receipt.ShouldNotBeNull();
            receipt.RootElement.TryGetProperty("StatementParameterTypes", out var parameterTypes).ShouldBeTrue();
            parameterTypes.GetArrayLength().ShouldBe(2);
            parameterTypes[0][0].GetString().ShouldBe("cents");
            parameterTypes[1][0].GetString().ShouldBe("cents");
        }
        var reopened = SqlCatalog.Open(storage).Scripts.ShouldHaveSingleItem();
        reopened.StatementParameterTypes.Count.ShouldBe(2);
        reopened.StatementParameterTypes[0].ShouldBe(["cents"]);
        reopened.StatementParameterTypes[1].ShouldBe(["cents"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - A receipt cannot change its preceding parameter type claims")]
    public async Task CompleteScriptStatementAsync_ChangedPrecedingParameterTypes_ShouldRefusePublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-altered-types");
        var catalog = SqlCatalog.Open(storage);
        var function = new SqlCatalogFunction("identity", "\"IDENTITY\"(5)", ["p"], ["cents"], "BIGINT", "IMMUTABLE", false, "p", [], DatabaseObjectOwner.Schema, "App");
        var create = new SqlCatalogScriptPublication("001", new string('A', 64), 2, null, catalog.DdlGeneration, null, "F:" + function.Signature);
        await catalog.CreateFunctionAsync(function, create, CancellationToken.None);
        var previous = catalog.Scripts.ShouldHaveSingleItem();
        var altered = new SqlCatalogScript(previous.Name, previous.ContentHash, previous.StatementCount, previous.CompletedStatements,
            previous.BaseGeneration, previous.Generation, previous.SchemaHash, previous.Objects, previous.StatementObjects, [["different_domain"]]);
        var publication = new SqlCatalogScriptPublication(previous.Name, previous.ContentHash, 2, altered, catalog.DdlGeneration, null, string.Empty);

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CompleteScriptStatementAsync(publication, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Scripts.ShouldHaveSingleItem().ShouldBe(previous);
        catalog.DdlGeneration.ShouldBe(previous.Generation);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Malformed receipt parameter vectors have a coded refusal")]
    [InlineData("[]")]
    [InlineData("[null]")]
    public void Open_MalformedReceiptParameterTypes_ShouldRefuseWithCode(string types)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-invalid-types");
        string json = "{\"Name\":\"001\",\"ContentHash\":\"" + new string('A', 64) +
            "\",\"StatementCount\":1,\"CompletedStatements\":1,\"BaseGeneration\":0,\"Generation\":0," +
            "\"SchemaHash\":null,\"Objects\":[],\"StatementObjects\":[\"F:\\\"IDENTITY\\\"(5)\"],\"StatementParameterTypes\":" + types + "}";
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, new DatabaseKeyWriter().AppendInt32(12).AppendString(json, Collation.Binary).ToArray());
            transaction.Commit();
        }

        // Act
        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - A receipt cannot certify a different mutation")]
    public async Task CreateTableAsync_MismatchedReceiptTarget_ShouldRefusePublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-wrong-target");
        var catalog = SqlCatalog.Open(storage);
        ulong generation = catalog.DdlGeneration;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("CREATE TABLE wanted (id INT)")));
        var publication = new SqlCatalogScriptPublication("wanted", hash, 1, null, generation, null, "T:\"dbo\".\"wanted\"");

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync("dbo", "unrelated",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], null, publication, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Tables.ShouldBeEmpty();
        catalog.Scripts.ShouldBeEmpty();
        catalog.DdlGeneration.ShouldBe(generation);
        var reopened = SqlCatalog.Open(storage);
        reopened.Tables.ShouldBeEmpty();
        reopened.Scripts.ShouldBeEmpty();
        reopened.DdlGeneration.ShouldBe(generation);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - A matching receipt target cannot use stale preceding state")]
    public async Task CreateTableAsync_StalePrecedingReceipt_ShouldRefusePublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-stale-state");
        var catalog = SqlCatalog.Open(storage);
        var previous = await catalog.BeginScriptAsync("001", new string('A', 64), 1, null, CancellationToken.None);
        var reservation = new SqlCatalogScriptPublication("001", previous.ContentHash, 1, previous, catalog.DdlGeneration, null, "T:\"dbo\".\"wanted\"");
        await catalog.ReserveTableAsync("dbo", "wanted", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)],
            null, [], DatabaseObjectOwner.Schema, "App", reservation, CancellationToken.None);
        var current = catalog.Scripts.ShouldHaveSingleItem();
        var stale = new SqlCatalogScriptPublication("001", previous.ContentHash, 1, previous, catalog.DdlGeneration, null, "T:\"dbo\".\"wanted\"");

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync("dbo", "wanted",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], null, stale, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Tables.ShouldBeEmpty();
        catalog.Scripts.ShouldHaveSingleItem().ShouldBe(current);
        catalog.DdlGeneration.ShouldBe(current.Generation);
        var reopened = SqlCatalog.Open(storage);
        reopened.Tables.ShouldBeEmpty();
        reopened.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(0);
        reopened.DdlGeneration.ShouldBe(current.Generation);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Cancellation is the final publication parameter")]
    public void PublicationMethods_CancellationParameter_ShouldBeLast()
    {
        // Arrange
        var methods = typeof(SqlCatalog).GetMethods().Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)));

        // Act
        var invalid = methods.Where(method => method.GetParameters()[^1].ParameterType != typeof(CancellationToken)).Select(method => method.Name).ToArray();
        var optionalPublication = methods.SelectMany(method => method.GetParameters()).Where(parameter =>
            parameter.ParameterType == typeof(SqlCatalogScript) || parameter.ParameterType == typeof(SqlCatalogScriptPublication) && parameter.IsOptional).ToArray();

        // Assert
        invalid.ShouldBeEmpty();
        optionalPublication.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - A publication refuses inconsistent script identity atomically")]
    [InlineData("checksum")]
    [InlineData("count")]
    [InlineData("origin")]
    [InlineData("futureorigin")]
    [InlineData("schema")]
    [InlineData("claims")]
    public async Task CreateTableAsync_InconsistentReceipt_ShouldRefusePublication(string changed)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-identity");
        var catalog = SqlCatalog.Open(storage);
        ulong generation = catalog.DdlGeneration;
        var original = await catalog.BeginScriptAsync("001", new string('A', 64), 1, null, CancellationToken.None);
        ulong suppliedOrigin = changed == "futureorigin" ? generation + 2 : changed == "origin" ? generation + 1 : generation;
        var preceding = new SqlCatalogScript("001", original.ContentHash, 1, 0, suppliedOrigin, suppliedOrigin, null,
            changed == "claims" ? ["T:\"dbo\".\"fake\""] : []);
        var invalid = new SqlCatalogScriptPublication("001", new string(changed == "checksum" ? 'B' : 'A', 64), changed == "count" ? 2 : 1,
            preceding, generation, changed == "schema" ? "different" : null, "T:\"dbo\".\"t\"");

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync("dbo", "t",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], null, invalid, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Tables.ShouldBeEmpty();
        catalog.Scripts.ShouldHaveSingleItem().ShouldBe(original);
        catalog.DdlGeneration.ShouldBe(generation);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Reservation records progress only when its table publishes")]
    public async Task ReserveTableAsync_GivenReceipt_ShouldDeferCompletionUntilPublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-reservation");
        var catalog = SqlCatalog.Open(storage);
        var publication = new SqlCatalogScriptPublication("001", new string('A', 64), 1, null, catalog.DdlGeneration, null, "T:\"dbo\".\"t\"");

        // Act
        var table = await catalog.ReserveTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)],
            null, [], DatabaseObjectOwner.Schema, "App", publication, CancellationToken.None);

        // Assert: the reservation is durable but the statement has not published.
        var reserved = catalog.Scripts.ShouldHaveSingleItem();
        reserved.CompletedStatements.ShouldBe(0);
        reserved.Objects.ShouldBeEmpty();
        reserved.StatementObjects.ShouldBeEmpty();
        catalog.Tables.ShouldBeEmpty();
        var refreshed = new SqlCatalogScriptPublication("001", publication.ContentHash, 1, reserved, catalog.DdlGeneration, null, "T:\"dbo\".\"t\"");
        await catalog.PublishTableAsync(table, [], [], false, refreshed, CancellationToken.None);
        catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(1);
        catalog.Scripts.ShouldHaveSingleItem().Generation.ShouldBe(catalog.DdlGeneration);
        catalog.Scripts.ShouldHaveSingleItem().StatementObjects.ShouldBe(["T:\"dbo\".\"t\""]);
        catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldBe(["T:\"dbo\".\"t\""]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - A public table publication completes only its given script receipt")]
    public async Task CreateTableAsync_GivenReceipt_ShouldCompleteOnlyThatPublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-receipt");
        var catalog = SqlCatalog.Open(storage);
        var receipt = new SqlCatalogScriptPublication("001", new string('A', 64), 1, null, catalog.DdlGeneration, null, "T:\"dbo\".\"t\"");

        // Act
        await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], null, receipt, CancellationToken.None);
        ulong publication = catalog.DdlGeneration;
        await catalog.CreateTableAsync("dbo", "unrelated", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None);

        // Assert
        catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(1);
        catalog.Scripts.ShouldHaveSingleItem().Generation.ShouldBe(publication);
        catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldBe(["T:\"dbo\".\"t\""]);
        catalog.Scripts.ShouldHaveSingleItem().StatementObjects.ShouldBe(["T:\"dbo\".\"t\""]);
        catalog.Tables.Count.ShouldBe(2);
        await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync("dbo", "reused_receipt",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], null, receipt, CancellationToken.None));
        catalog.Tables.Count.ShouldBe(2);
        catalog.Scripts.ShouldHaveSingleItem().Generation.ShouldBe(publication);
        SqlCatalog.Open(storage).Scripts.ShouldHaveSingleItem().StatementObjects.ShouldBe(["T:\"dbo\".\"t\""]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Non-DDL completion refuses object claims")]
    public async Task CompleteScriptStatementAsync_DdlTarget_ShouldRefusePublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-data");
        var catalog = SqlCatalog.Open(storage);
        var previous = await catalog.BeginScriptAsync("001", new string('A', 64), 1, null, CancellationToken.None);
        var publication = new SqlCatalogScriptPublication("001", previous.ContentHash, 1, previous, catalog.DdlGeneration, null, "T:\"dbo\".\"t\"");

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CompleteScriptStatementAsync(publication, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Scripts.ShouldHaveSingleItem().ShouldBe(previous);
        var read = new SqlCatalogScriptPublication("001", previous.ContentHash, 1, previous, catalog.DdlGeneration, null, string.Empty);
        await catalog.CompleteScriptStatementAsync(read, CancellationToken.None);
        catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(1);
        catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldBeEmpty();
        catalog.Scripts.ShouldHaveSingleItem().StatementObjects.ShouldBe([string.Empty]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Receipt targets follow committed table and definition mutations")]
    public async Task PublicationMethods_MixedMutations_ShouldDeriveOrderedTargets()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-targets");
        var catalog = SqlCatalog.Open(storage);
        const int statementCount = 8;
        string hash = new('A', 64);
        await catalog.BeginScriptAsync("001", hash, statementCount, null, CancellationToken.None);
        SqlCatalogScriptPublication Next(string target) => new("001", hash, statementCount,
            catalog.Scripts.ShouldHaveSingleItem(), catalog.DdlGeneration, null, target);
        var column = new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true);
        var domain = new SqlCatalogDomain("cents", "BIGINT", null, false, null, null, [], DatabaseObjectOwner.Schema, "App");
        var function = new SqlCatalogFunction("identity", "\"IDENTITY\"(5)", ["p"], ["BIGINT"], "BIGINT", "IMMUTABLE", false, "p", [], DatabaseObjectOwner.Schema, "App");

        // Act
        await catalog.CreateTableAsync("dbo", "t", [column], null, Next("T:\"dbo\".\"t\""), CancellationToken.None);
        await catalog.AddColumnAsync("dbo", "t", new SqlCatalogColumn("extra", new DatabaseTypeInfo(DatabaseType.Int32), true), Next("T:\"dbo\".\"t\""), CancellationToken.None);
        await catalog.DropColumnAsync("dbo", "t", "extra", Next("T:\"dbo\".\"t\""), CancellationToken.None);
        await catalog.DropTableAsync("dbo", "t", Next("T:\"dbo\".\"t\""), CancellationToken.None);
        await catalog.CreateDomainAsync(domain, Next("D:\"cents\""), CancellationToken.None);
        await catalog.CreateFunctionAsync(function, Next("F:\"IDENTITY\"(5)"), CancellationToken.None);
        await catalog.DropFunctionAsync(function.Signature, function.ParameterTypes, Next("F:\"IDENTITY\"(5)"), CancellationToken.None);
        await catalog.DropDomainAsync(domain.Name, Next("D:\"cents\""), CancellationToken.None);

        // Assert
        var receipt = SqlCatalog.Open(storage).Scripts.ShouldHaveSingleItem();
        receipt.CompletedStatements.ShouldBe(statementCount);
        receipt.Objects.ShouldBe(["T:\"dbo\".\"t\""]);
        receipt.StatementObjects.ShouldBe(["T:\"dbo\".\"t\"", "T:\"dbo\".\"t\"", "T:\"dbo\".\"t\"", "T:\"dbo\".\"t\"", "D:\"cents\"", "F:\"IDENTITY\"(5)", "F:\"IDENTITY\"(5)", "D:\"cents\""]);
        receipt.Generation.ShouldBe(catalog.DdlGeneration);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Unrelated DDL invalidates an expected publication generation")]
    public async Task CreateTableAsync_InterveningDdl_ShouldRefusePublication()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "script-generation");
        var catalog = SqlCatalog.Open(storage);
        var previous = await catalog.BeginScriptAsync("001", new string('A', 64), 1, null, CancellationToken.None);
        var publication = new SqlCatalogScriptPublication("001", previous.ContentHash, 1, previous, catalog.DdlGeneration, null, "T:\"dbo\".\"wanted\"");
        await catalog.CreateTableAsync("dbo", "intervening", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], cancellationToken: CancellationToken.None);
        ulong generation = catalog.DdlGeneration;

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.CreateTableAsync("dbo", "wanted",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], null, publication, CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("intervening");
        catalog.Scripts.ShouldHaveSingleItem().ShouldBe(previous);
        catalog.DdlGeneration.ShouldBe(generation);
    }
}
