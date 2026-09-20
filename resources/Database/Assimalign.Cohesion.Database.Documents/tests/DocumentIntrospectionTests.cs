using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentIntrospectionTests
{
    [Fact]
    public async Task Index_definitions_are_queryable_documents_and_follow_catalog_ddl()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("inventory");
        await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        (await Rows(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        await session.ExecuteAsync("CREATE INDEX by_city ON items (addresses[0]['postal-code'])");
        await session.ExecuteAsync("CREATE INDEX by_name ON items (name)");

        var rows = await Rows(session, "SELECT i.INDEX_NAME, i.PATH, i.IS_UNIQUE FROM COHESION_SCHEMA.INDEXES AS i WHERE i.COLLECTION_NAME = @collection ORDER BY i.INDEX_NAME",
            new Dictionary<string, object?> { ["collection"] = "items" });
        rows.Select(row => row.GetString(0)).ShouldBe(["by_city", "by_name"]);
        rows[0].GetString(1).ShouldBe("addresses[0]['postal-code']");
        rows[0].GetBoolean(2).ShouldBeFalse();
        var whole = (JsonElement)(await Rows(session, "SELECT * FROM cohesion_schema.indexes WHERE INDEX_NAME = 'by_city'"))
            .Single().GetValue(0)!;
        whole.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["COLLECTION_CATALOG", "COLLECTION_NAME", "INDEX_NAME", "PATH", "IS_UNIQUE"]);
        whole.GetProperty("COLLECTION_CATALOG").GetString().ShouldBe("inventory");
        whole.GetProperty("COLLECTION_NAME").GetString().ShouldBe("items");
        var groups = await Rows(session, "SELECT COLLECTION_NAME, COUNT(*) AS total FROM COHESION_SCHEMA.INDEXES GROUP BY COLLECTION_NAME HAVING COUNT(*) > 1 ORDER BY total");
        groups.Single().GetInt32(1).ShouldBe(2);

        await session.ExecuteAsync("DROP INDEX by_city ON items");
        (await Rows(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES")).Single().GetString(0).ShouldBe("by_name");
        await database.DropCollectionAsync("items");
        (await Rows(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        (await Rows(session, "SELECT * FROM COHESION_SCHEMA.OBJECT_OWNERSHIP")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Ownership_reports_the_collection_authority_without_inventing_index_ownership()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("inventory");
        await database.CreateCollectionAsync("adhoc");
        await database.CreateCollectionAsync("owned");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE INDEX by_name ON owned (name)");
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var metadata = database.Catalog.FindCollection("owned", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context);
        await database.Coordinator.CommitAsync(context);

        var rows = await Rows(session, "SELECT COLLECTION_CATALOG, COLLECTION_NAME, OBJECT_TYPE, OBJECT_NAME, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY COLLECTION_NAME");
        rows.Count.ShouldBe(2);
        rows[0].GetString(0).ShouldBe("inventory");
        rows[0].GetString(1).ShouldBe("adhoc");
        rows[0].GetString(2).ShouldBe("COLLECTION");
        rows[0].GetString(3).ShouldBe("adhoc");
        rows[0].GetString(4).ShouldBe("Adhoc");
        rows[0].IsNull(5).ShouldBeTrue();
        rows[1].GetString(3).ShouldBe("owned");
        rows[1].GetString(4).ShouldBe("Schema");
        rows[1].GetString(5).ShouldBe("Sales");
        (await Rows(session, "SELECT COLLECTION_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE OWNING_SCHEMA IS NULL"))
            .Single().GetString(0).ShouldBe("adhoc");
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
            await session.ExecuteAsync("DROP INDEX by_name ON owned"));
        error.Message.ShouldContain(rows[1].GetString(5)!);

        var collections = new List<string>();
        await foreach (var collection in database.GetCollectionsAsync()) { collections.Add(collection.Name); }
        collections.ShouldBe(["adhoc", "owned"]);
    }

    [Fact]
    public async Task Introspection_uses_the_statement_snapshot_and_sees_own_uncommitted_definitions()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("inventory");
        await database.CreateCollectionAsync("items");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await using (var transaction = await reader.BeginTransactionAsync())
        {
            await writer.ExecuteAsync("CREATE INDEX by_name ON items (name)");
            (await Rows(reader, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
            await transaction.CommitAsync();
        }
        (await Rows(reader, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES")).Single().GetString(0).ShouldBe("by_name");
        await using (var transaction = await reader.BeginTransactionAsync())
        {
            await reader.ExecuteAsync("CREATE INDEX temporary ON items (price)");
            (await Rows(reader, "SELECT COUNT(*) FROM COHESION_SCHEMA.INDEXES")).Single().GetInt32(0).ShouldBe(2);
            (await Rows(writer, "SELECT COUNT(*) FROM COHESION_SCHEMA.INDEXES")).Single().GetInt32(0).ShouldBe(1);
            await transaction.RollbackAsync();
        }
        (await Rows(reader, "SELECT COUNT(*) FROM COHESION_SCHEMA.INDEXES")).Single().GetInt32(0).ShouldBe(1);
    }

    [Theory]
    [InlineData("COHESION_SCHEMA.INDEXES")]
    [InlineData("COHESION_SCHEMA.OBJECT_OWNERSHIP")]
    public async Task System_collections_refuse_every_supported_mutation_with_a_stable_diagnostic(string name)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("inventory");
        await using var session = await database.CreateSessionAsync();
        string expected = $"System collection '{name}' is read-only.";
        foreach (string command in new[] { $"CREATE INDEX injected ON {name.ToLowerInvariant()} (x)", $"DROP INDEX absent ON \"{name}\"" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(command));
            error.Message.ShouldBe(expected);
        }
        var scoped = (IDocumentDatabase)session.Database;
        (await Should.ThrowAsync<DatabaseException>(async () => await scoped.CreateCollectionAsync(name))).Message.ShouldBe(expected);
        (await Should.ThrowAsync<DatabaseException>(async () => await scoped.DropCollectionAsync(name))).Message.ShouldBe(expected);
        (await Should.ThrowAsync<DatabaseException>(async () => await database.CreateCollectionAsync(name.ToLowerInvariant()))).Message.ShouldBe(expected);
        (await Should.ThrowAsync<DatabaseException>(async () => await database.DropCollectionAsync(name.ToLowerInvariant()))).Message.ShouldBe(expected);
    }

    [Theory]
    [InlineData("INSERT INTO COHESION_SCHEMA.INDEXES VALUES (1)")]
    [InlineData("UPDATE COHESION_SCHEMA.OBJECT_OWNERSHIP SET OWNER = 'Adhoc'")]
    [InlineData("DELETE FROM COHESION_SCHEMA.INDEXES")]
    public async Task Unsupported_document_data_mutations_keep_the_language_capability_diagnostic(string query)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("inventory");
        await using var session = await database.CreateSessionAsync();
        (await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(query))).Message.ShouldContain("COHDBL001");
    }

    [Fact]
    public async Task System_collections_are_database_scoped_even_when_collection_and_index_names_match()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var own = (IDocumentDatabase)await engine.CreateDatabaseAsync("own");
        var other = (IDocumentDatabase)await engine.CreateDatabaseAsync("other");
        await own.CreateCollectionAsync("items");
        await other.CreateCollectionAsync("items");
        await other.CreateCollectionAsync("private");
        await using var ownSession = await own.CreateSessionAsync();
        await using var otherSession = await other.CreateSessionAsync();
        await ownSession.ExecuteAsync("CREATE INDEX by_value ON items (ownValue)");
        await otherSession.ExecuteAsync("CREATE INDEX by_value ON items (otherValue)");
        var rows = await Rows(ownSession, "SELECT COLLECTION_CATALOG, PATH FROM COHESION_SCHEMA.INDEXES");
        rows.Single().GetString(0).ShouldBe("own");
        rows.Single().GetString(1).ShouldBe("ownValue");
        (await Rows(ownSession, "SELECT COLLECTION_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP")).Single().GetString(0).ShouldBe("items");
        (await Rows(otherSession, "SELECT PATH FROM COHESION_SCHEMA.INDEXES")).Single().GetString(0).ShouldBe("otherValue");
        await Should.ThrowAsync<DatabaseParseException>(async () => await ownSession.ExecuteAsync("SELECT * FROM other.COHESION_SCHEMA.INDEXES"));
    }

    private static async Task<List<QueryRow>> Rows(IDatabaseSession session, string query, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(query, parameters);
        var rows = new List<QueryRow>();
        await foreach (var row in result.GetRowsAsync()) { rows.Add(row); }
        return rows;
    }
}
