using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentAccessCostTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Selective probes beat broad equality and index-name ties (#1527)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RareIndexedPredicate_ShouldBoundJsonReads(bool equality)
    {
        await using var engine = DocumentDatabaseEngine.Create("document-access-cost", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", cancellationToken: CancellationToken.None);
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            for (int i = 0; i < 600; i++)
            {
                await collection.PutAsync(session, i.ToString("D3"), Encoding.UTF8.GetBytes(
                    $"{{\"status\":\"{(i < 550 ? "open" : "closed")}\",\"stamp\":{i},\"rare\":{(i < 4 ? 1 : 0)}}}"),
                    cancellationToken: CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        await session.ExecuteAsync("CREATE INDEX a_status ON items(status)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync(equality ? "CREATE INDEX z_selective ON items(rare)" : "CREATE INDEX z_selective ON items(stamp)",
            cancellationToken: CancellationToken.None);
        string query = equality
            ? "SELECT META().id AS id FROM items WHERE status='open' AND rare=1"
            : "SELECT META().id AS id FROM items WHERE status='open' AND stamp>=5 AND stamp<9";
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var ids = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { ids.Add(row.GetString(0).ShouldNotBeNull()); }
        ids.ShouldBe(Enumerable.Range(equality ? 0 : 5, 4).Select(i => i.ToString("D3")));
        session.LastQueryDocumentsRead.ShouldBeLessThanOrEqualTo(8,
            "Four selective index candidates permit at most twice that many JSON reads.");
        await using var description = (await session.ExecuteAsync("EXPLAIN " + query,
            cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        session.LastQueryDocumentsRead.ShouldBe(0);
        var seek = (await NodesAsync(description)).Single(node => node.Operator == "IndexSeek");
        seek.Index.ShouldBe("z_selective");
        seek.EstimatedRows.ShouldBe(4);
        seek.ActualRows.ShouldBeNull();
        seek.Detail.ShouldNotBeNull().ShouldContain("estimate_kind=exact_index_entries", Case.Sensitive);
        await using var broad = (await session.ExecuteAsync("EXPLAIN SELECT META().id AS id FROM items WHERE status='open'",
            cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var lowerBound = (await NodesAsync(broad)).Single(node => node.Operator == "IndexSeek");
        lowerBound.EstimatedRows.ShouldBe(512);
        lowerBound.Detail.ShouldNotBeNull().ShouldContain("estimate_kind=lower_bound; probe_entries=512;", Case.Sensitive);
        session.LastQueryDocumentsRead.ShouldBe(0);
        await session.ExecuteAsync(equality ? "CREATE INDEX zz_compound ON items(status,rare)" : "CREATE INDEX zz_compound ON items(status,stamp)",
            cancellationToken: CancellationToken.None);
        await using var compound = (await session.ExecuteAsync("EXPLAIN " + query,
            cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var compositeSeek = (await NodesAsync(compound)).Single(node => node.Operator == "IndexSeek");
        compositeSeek.Index.ShouldBe("zz_compound");
        compositeSeek.EstimatedRows.ShouldBe(4);
        (await IdsAsync(session, query)).ShouldBe(ids);
        session.LastQueryDocumentsRead.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Intersection filters identities before JSON and shares DML access (#1527)")]
    public async Task ExecuteAsync_ComparableIndexes_ShouldIntersectBeforeJsonAndPreserveRollback()
    {
        await using var engine = DocumentDatabaseEngine.Create("document-intersection", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", cancellationToken: CancellationToken.None);
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            for (int i = 0; i < 200; i++)
            {
                await collection.PutAsync(session, i.ToString("D3"), Encoding.UTF8.GetBytes(
                    $"{{\"a\":{(i < 100 ? 1 : 0)},\"b\":{(i is >= 99 and < 199 ? 1 : 0)},\"residual\":true}}"),
                    cancellationToken: CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        const string predicate = "a=1 AND b=1 AND residual=true";
        const string query = "SELECT META().id AS id FROM items WHERE " + predicate;
        (await IdsAsync(session, query)).ShouldBe(["099"]);
        foreach (string path in new[] { "a", "b" })
        { await session.ExecuteAsync($"CREATE INDEX by_{path} ON items({path})", cancellationToken: CancellationToken.None); }

        (await IdsAsync(session, query)).ShouldBe(["099"]);
        session.LastQueryDocumentsRead.ShouldBe(1);
        (await IdsAsync(session, query.Replace("residual=true", "residual=false", StringComparison.Ordinal))).ShouldBeEmpty();
        session.LastQueryDocumentsRead.ShouldBe(1);
        await using (var description = (await session.ExecuteAsync("EXPLAIN " + query,
            cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>())
        {
            session.LastQueryDocumentsRead.ShouldBe(0);
            var nodes = await NodesAsync(description);
            var intersection = nodes.Single(node => node.Operator == "IndexIntersection");
            intersection.EstimatedRows.ShouldBe(100);
            intersection.ActualRows.ShouldBeNull();
            intersection.Detail.ShouldBe("inputs=2; estimate_kind=upper_bound");
            var inputs = nodes.Where(node => node.ParentId == intersection.NodeId).ToArray();
            inputs.Select(node => node.Index).ShouldBe(["by_a", "by_b"]);
            inputs.All(node => node.EstimatedRows == 100 && node.ActualRows is null).ShouldBeTrue();
        }
        foreach (string mutation in new[] { "UPDATE items SET a=0 WHERE " + predicate, "DELETE FROM items WHERE " + predicate })
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
            var result = await session.ExecuteAsync(mutation, cancellationToken: CancellationToken.None);
            result.AffectedCount.ShouldBe(1);
            session.LastQueryDocumentsRead.ShouldBe(1);
            (await IdsAsync(session, query)).ShouldBeEmpty();
            await transaction.RollbackAsync(CancellationToken.None);
            (await IdsAsync(session, query)).ShouldBe(["099"]);
        }
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        var writerCollection = (await writer.GetCollectionAsync("items", CancellationToken.None)).ShouldNotBeNull();
        await using (var snapshot = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None))
        {
            (await IdsAsync(session, query)).ShouldBe(["099"]);
            await writerCollection.PutAsync(writer, "099", "{\"a\":0,\"b\":1,\"residual\":true}"u8.ToArray(), cancellationToken: CancellationToken.None);
            await writerCollection.PutAsync(writer, "200", "{\"a\":1,\"b\":1,\"residual\":true}"u8.ToArray(), cancellationToken: CancellationToken.None);
            (await IdsAsync(session, query)).ShouldBe(["099"]);
            session.LastQueryDocumentsRead.ShouldBe(1);
            (await IdsAsync(writer, query)).ShouldBe(["200"]);
            await snapshot.RollbackAsync(CancellationToken.None);
        }
        await using (var ownWrites = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            await collection.PutAsync(session, "201", "{\"a\":1,\"b\":1,\"residual\":true}"u8.ToArray(), cancellationToken: CancellationToken.None);
            (await IdsAsync(session, query)).ShouldBe(["200", "201"]);
            session.LastQueryDocumentsRead.ShouldBe(2);
            await ownWrites.RollbackAsync(CancellationToken.None);
        }
        (await IdsAsync(session, query)).ShouldBe(["200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Wide-key estimates stop at the physical page budget (#1527)")]
    public async Task ExecuteAsync_WideIndexProbe_ShouldRespectPageBudget()
    {
        await using var engine = DocumentDatabaseEngine.Create("document-probe-pages", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", cancellationToken: CancellationToken.None);
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            for (int i = 0; i < 600; i++)
            {
                await collection.PutAsync(session, i.ToString("D3"), Encoding.UTF8.GetBytes(
                    $"{{\"wide\":\"x{new string('a', 250)}{i:D3}\"}}"), cancellationToken: CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        await session.ExecuteAsync("CREATE INDEX by_wide ON items(wide)", cancellationToken: CancellationToken.None);
        await using var description = (await session.ExecuteAsync("EXPLAIN SELECT META().id AS id FROM items WHERE wide>='x' AND wide<'y'",
            cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var seek = (await NodesAsync(description)).Single(node => node.Operator == "IndexSeek");
        seek.EstimatedRows.ShouldNotBeNull().ShouldBeInRange(1, 511);
        seek.Detail.ShouldNotBeNull().ShouldContain("estimate_kind=lower_bound", Case.Sensitive);
        seek.Detail.ShouldNotBeNull().ShouldEndWith("probe_pages=32", Case.Sensitive);
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Intersection inputs are bounded and remaining conjuncts stay residual (#1527)")]
    public async Task ExecuteAsync_FiveComparableIndexes_ShouldUseFourInputsAndFilterTheFifth()
    {
        await using var engine = DocumentDatabaseEngine.Create("document-intersection-bound", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", cancellationToken: CancellationToken.None);
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            for (int i = 0; i < 256; i++)
            {
                string json = "{" + string.Join(',', Enumerable.Range(0, 5).Select(bit => $"\"p{bit}\":{(i >> bit) & 1}")) + "}";
                await collection.PutAsync(session, i.ToString("D3"), Encoding.UTF8.GetBytes(json), cancellationToken: CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        for (int i = 0; i < 5; i++)
        { await session.ExecuteAsync($"CREATE INDEX by_p{i} ON items(p{i})", cancellationToken: CancellationToken.None); }
        string query = "SELECT META().id AS id FROM items WHERE " + string.Join(" AND ", Enumerable.Range(0, 5).Select(bit => $"p{bit}=1"));
        (await IdsAsync(session, query)).ShouldBe(Enumerable.Range(0, 256).Where(i => (i & 31) == 31).Select(i => i.ToString("D3")));
        session.LastQueryDocumentsRead.ShouldBe(16);
        await using var description = (await session.ExecuteAsync("EXPLAIN " + query,
            cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var nodes = await NodesAsync(description);
        var intersection = nodes.Single(node => node.Operator == "IndexIntersection");
        intersection.Detail.ShouldBe("inputs=4; estimate_kind=upper_bound");
        nodes.Where(node => node.ParentId == intersection.NodeId).Select(node => node.Index).ShouldBe(["by_p0", "by_p1", "by_p2", "by_p3"]);
    }

    private static async Task<string[]> IdsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var ids = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { ids.Add(row.GetString(0).ShouldNotBeNull()); }
        return ids.ToArray();
    }

    private static async Task<List<DatabasePlanNode>> NodesAsync(DatabasePlanDescription description)
    {
        var nodes = new List<DatabasePlanNode>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None))
        {
            nodes.Add(new DatabasePlanNode
            {
                NodeId = row.GetInt32(0), ParentId = row.IsNull(1) ? null : row.GetInt32(1),
                Operator = row.GetString(2).ShouldNotBeNull(), Index = row.GetString(4), Detail = row.GetString(5),
                EstimatedRows = row.IsNull(6) ? null : row.GetInt64(6), ActualRows = row.IsNull(7) ? null : row.GetInt64(7),
            });
        }
        return nodes;
    }
}
