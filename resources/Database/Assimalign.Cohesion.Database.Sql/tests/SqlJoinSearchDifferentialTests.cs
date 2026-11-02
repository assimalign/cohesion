using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlJoinSearchDifferentialTests
{
    private readonly ITestOutputHelper _output;

    public SqlJoinSearchDifferentialTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search fix: exact corpus preserves b6224ec66 order, cost and counters (#1629)")]
    public async Task Cost_ExactGeneratedCorpus_ShouldPreservePlans()
    {
        // Arrange: powers-of-two minus one make index work exact on every platform.
        await using var engine = SqlDatabaseEngine.Create("search-differential", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        int[] counts = [31, 7, 3, 1, 31, 7, 3, 1];
        for (int i = 0; i < counts.Length; i++)
        {
            await session.ExecuteAsync($"CREATE TABLE t{i}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO t{i} VALUES" + string.Join(',', Enumerable.Range(1, counts[i]).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        }
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);
            var fingerprints = new StringBuilder();
            int cases = 0;

            // Act
            for (int count = 2; count <= 8; count++)
            {
                foreach (string shape in new[] { "chain", "star", "clique", "disconnected" })
                {
                    foreach (string physical in new[] { "loop", "hash", "index" })
                    {
                        // Uniform loop inputs isolate allocation changes from the
                        // corrected buffer tie-break. Hash/index paths mix estimates.
                        bool uniform = physical == "loop" || count == 3 && shape == "disconnected";
                        string Table(int i) => uniform ? $"t0 t{i}" : $"t{i}";
                        string key = physical == "index" ? "id" : "k";
                        var terms = new List<string>();
                        string from = Table(0);
                        for (int right = 1; right < count; right++)
                        {
                            var crossing = new List<string>();
                            if (shape == "clique")
                            { for (int left = 0; left < right; left++) { crossing.Add($"t{left}.{key}=t{right}.{key}"); } }
                            else if (shape != "disconnected" || right != count / 2)
                            { crossing.Add($"t{(shape == "star" ? 0 : right - 1)}.{key}=t{right}.{key}"); }
                            if (physical != "loop" && crossing.Count != 0) { from += $" JOIN {Table(right)} ON " + string.Join(" AND ", crossing); }
                            else { from += $" CROSS JOIN {Table(right)}"; terms.AddRange(crossing); }
                        }
                        string query = "SELECT t0.id FROM " + from + (terms.Count == 0 ? "" : " WHERE " + string.Join(" AND ", terms));
                        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(query).Statement.SqlExpression);
                        var selected = executor.Cost(written, context, CancellationToken.None).ShouldBeAssignableTo<SqlJoinPlan>();
                        string record = $"{count}/{shape}/{physical}|";
                        if (selected is SqlSearchedJoinPlan searched)
                        {
                            record += $"{searched.ChosenOrder}|{searched.SearchCost.ToString("R", CultureInfo.InvariantCulture)}|{searched.States}|{searched.Partitions}|{searched.Reordered}";
                        }
                        else { record += $"bound:{selected.FirstBinding}:" + string.Join(',', selected.Steps.Select(step => step.Binding)); }
                        record += "|" + string.Join(',', selected.Steps.Select(step => step.Hash != null ? "H" : step.Access != null ? "I" : "L"));
                        _output.WriteLine(record);
                        fingerprints.Append(record).Append('\n');
                        cases++;
                    }
                }
            }
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprints.ToString())));
            _output.WriteLine($"cases={cases}; fingerprint={digest}");

            // Assert: recorded against unmodified b6224ec66, before the allocation refactor.
            cases.ShouldBe(84);
            digest.ShouldBe("ABE0F8BC91739278A0C7AF0A3D8D1CF97CB9862464AB38266F2B38DF5EBD2B1D");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }
}
