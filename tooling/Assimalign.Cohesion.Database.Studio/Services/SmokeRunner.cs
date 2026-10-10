using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// Headless round trips through the same workspace classes the UI uses: for each model a tiny
/// create/insert/query both embedded and (where a server exists) over loopback TCP, plus every
/// sample script in embedded mode. One PASS/FAIL/SKIP line per step.
/// </summary>
internal sealed class SmokeRunner
{
    private const string databaseName = "smoke";

    private readonly Action<string> _write;
    private int _passed;
    private int _failed;
    private int _skipped;

    private SmokeRunner(Action<string> write)
    {
        _write = write;
    }

    public static async Task<int> RunAsync(string? dataRoot, Action<string> write)
    {
        var runner = new SmokeRunner(write);
        bool temporary = string.IsNullOrWhiteSpace(dataRoot);
        dataRoot = temporary
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cohesion-studio-smoke", Guid.NewGuid().ToString("N")[..8])
            : System.IO.Path.GetFullPath(dataRoot!);

        write($"Cohesion Database Studio smoke run  {DateTime.Now:O}");
        write($"data root: {dataRoot}{(temporary ? " (temporary)" : string.Empty)}");

        StudioEngines? engines = null;
        try
        {
            engines = await runner.StepAsync("engines", "create", () => Task.FromResult(StudioEngines.Create(dataRoot))).ConfigureAwait(false);
            if (engines is not null)
            {
                await runner.SqlAsync(engines).ConfigureAwait(false);
                await runner.DocumentsAsync(engines).ConfigureAwait(false);
                await runner.GraphAsync(engines).ConfigureAwait(false);
                await runner.KeyValueAsync(engines).ConfigureAwait(false);
                await runner.BlobAsync(engines).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The runner reports; it never lets an unexpected failure escape without a FAIL line.
            runner.Fail("smoke", "unhandled", ErrorText.Describe(exception), 0);
        }
        finally
        {
            if (engines is not null)
            {
                await runner.StepAsync("engines", "dispose", async () =>
                {
                    await engines.DisposeAsync().ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }

            if (temporary)
            {
                try
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of the temporary root.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        write($"SUMMARY: {runner._passed} passed, {runner._failed} failed, {runner._skipped} skipped");
        return runner._failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- SQL

    private async Task SqlAsync(StudioEngines engines)
    {
        var embedded = new SqlWorkspace(ConnectionMode.Embedded, engines, null);
        await using (embedded.ConfigureAwait(false))
        {
            if (await DeclaredDatabaseAsync("sql/declared", embedded).ConfigureAwait(false))
            {
                await DeclaredSqlSchemaAsync("sql/declared", embedded).ConfigureAwait(false);
            }

            const string scope = "sql/embedded";
            if (!await PrepareDatabaseAsync(scope, embedded).ConfigureAwait(false))
            {
                return;
            }

            await ScriptStepAsync(scope, "create-insert-select", embedded,
                "CREATE TABLE smoke_t (id BIGINT PRIMARY KEY, name TEXT NOT NULL); INSERT INTO smoke_t (id, name) VALUES (1, 'alpha'), (2, 'beta'); SELECT id, name FROM smoke_t ORDER BY id;",
                outcomes => Expect(outcomes[1].AffectedCount == 2, $"insert affected {outcomes[1].AffectedCount}")
                    ?? ExpectRows(outcomes[2], 2, row => row[1] == "beta")).ConfigureAwait(false);

            await RegisteredFunctionsStepAsync(scope, embedded, "smoke_t", rows: 2, lastInitial: "B", product: "2").ConfigureAwait(false);

            await StepAsync(scope, "transaction-api-rollback", async () =>
            {
                await embedded.BeginTransactionAsync(IsolationLevel.Snapshot).ConfigureAwait(false);
                Require(embedded.InTransaction, "transaction not active after Begin");
                await RunScriptAsync(embedded, "INSERT INTO smoke_t (id, name) VALUES (3, 'gamma');").ConfigureAwait(false);
                await embedded.RollbackAsync().ConfigureAwait(false);
                List<StatementOutcome> count = await RunScriptAsync(embedded, "SELECT COUNT(*) AS n FROM smoke_t;").ConfigureAwait(false);
                Require(count[0].Table?.Rows[0][0] == "2", $"expected 2 rows after rollback, got {count[0].Table?.Rows[0][0]}");
                return true;
            }).ConfigureAwait(false);

            await ScriptStepAsync(scope, "transaction-statements", embedded,
                "BEGIN; DELETE FROM smoke_t WHERE id = 1; ROLLBACK; SELECT COUNT(*) FROM smoke_t;",
                outcomes => ExpectRows(outcomes[3], 1, row => row[0] == "2")).ConfigureAwait(false);

            await CatalogStepAsync(scope, embedded, "smoke_t").ConfigureAwait(false);
            await SamplesAsync(scope, embedded).ConfigureAwait(false);
        }

        await WireAsync(engines, StudioModel.Sql, async (scope, endPoint) =>
        {
            var wire = new SqlWorkspace(ConnectionMode.WireLoopback, engines, endPoint);
            await using (wire.ConfigureAwait(false))
            {
                if (!await UseAsync(scope, wire).ConfigureAwait(false))
                {
                    return;
                }

                await ScriptStepAsync(scope, "create-insert-select", wire,
                    "CREATE TABLE smoke_w (id BIGINT PRIMARY KEY, name TEXT); INSERT INTO smoke_w (id, name) VALUES (1, 'one'), (2, 'two'), (3, 'three'); SELECT id, name FROM smoke_w WHERE id >= 2 ORDER BY id;",
                    outcomes => Expect(outcomes[1].AffectedCount == 3, $"insert affected {outcomes[1].AffectedCount}")
                        ?? ExpectRows(outcomes[2], 2, row => row[1] == "three")).ConfigureAwait(false);

                // The server runs statements on the same engine, so the registered functions answer over the wire too.
                await RegisteredFunctionsStepAsync(scope, wire, "smoke_w", rows: 3, lastInitial: "T", product: "6").ConfigureAwait(false);

                await ScriptStepAsync(scope, "begin-rollback-statements", wire,
                    "BEGIN; INSERT INTO smoke_w (id, name) VALUES (4, 'four'); ROLLBACK; SELECT COUNT(*) FROM smoke_w;",
                    outcomes => ExpectRows(outcomes[3], 1, row => row[0] == "3")).ConfigureAwait(false);

                await StepAsync(scope, "transaction-buttons", async () =>
                {
                    await wire.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
                    await RunScriptAsync(wire, "INSERT INTO smoke_w (id, name) VALUES (5, 'five');").ConfigureAwait(false);
                    await wire.CommitAsync().ConfigureAwait(false);
                    List<StatementOutcome> count = await RunScriptAsync(wire, "SELECT COUNT(*) FROM smoke_w;").ConfigureAwait(false);
                    Require(count[0].Table?.Rows[0][0] == "4", $"expected 4 rows after commit, got {count[0].Table?.Rows[0][0]}");
                    return true;
                }).ConfigureAwait(false);

                await CatalogStepAsync(scope, wire, "smoke_w").ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Documents

    private async Task DocumentsAsync(StudioEngines engines)
    {
        var embedded = new DocumentWorkspace(engines);
        await using (embedded.ConfigureAwait(false))
        {
            await DeclaredDatabaseAsync("documents/declared", embedded).ConfigureAwait(false);

            const string scope = "documents/embedded";
            if (!await PrepareDatabaseAsync(scope, embedded).ConfigureAwait(false))
            {
                return;
            }

            await StepAsync(scope, "seed-collection", async () =>
            {
                int count = await embedded.SeedSampleDataAsync().ConfigureAwait(false);
                IReadOnlyList<string> collections = await embedded.ListCollectionsAsync().ConfigureAwait(false);
                Require(collections.Contains("items"), "items collection missing");
                return count;
            }).ConfigureAwait(false);

            await StepAsync(scope, "put-get-delete", async () =>
            {
                Documents.Document saved = await embedded.PutAsync("items", "smoke", """{"name":"smoke","score":9}""", null).ConfigureAwait(false);
                Documents.Document? read = await embedded.GetAsync("items", "smoke").ConfigureAwait(false);
                Require(read is { } found && Encoding.UTF8.GetString(found.Content.Span).Contains("\"smoke\"", StringComparison.Ordinal), "document not read back");
                Require(await embedded.DeleteAsync("items", "smoke", saved.Version.Value).ConfigureAwait(false), "delete returned false");
                return true;
            }).ConfigureAwait(false);

            await ScriptStepAsync(scope, "oql-select", embedded,
                "SELECT d.name, d.score FROM items AS d WHERE d.score >= 3 ORDER BY d.score DESC;",
                outcomes => ExpectRows(outcomes[0], 2, row => row[0] == "anvil")).ConfigureAwait(false);

            await CatalogStepAsync(scope, embedded, "items").ConfigureAwait(false);
            await SamplesAsync(scope, embedded).ConfigureAwait(false);
        }

        Skip("documents/wire", "all", "Documents has no wire server or client (Documents.Client/src is empty)");
    }

    // ---------------------------------------------------------------- Graph

    private async Task GraphAsync(StudioEngines engines)
    {
        var embedded = new GraphWorkspace(ConnectionMode.Embedded, engines, null);
        await using (embedded.ConfigureAwait(false))
        {
            await DeclaredDatabaseAsync("graph/declared", embedded).ConfigureAwait(false);

            const string scope = "graph/embedded";
            if (!await PrepareDatabaseAsync(scope, embedded).ConfigureAwait(false))
            {
                return;
            }

            await GraphRoundTripAsync(scope, embedded, "Emb").ConfigureAwait(false);

            await StepAsync(scope, "transaction-api-rollback", async () =>
            {
                await embedded.BeginTransactionAsync(IsolationLevel.Snapshot).ConfigureAwait(false);
                await RunScriptAsync(embedded, "INSERT (:Smoke {name: 'ghost'});").ConfigureAwait(false);
                await embedded.RollbackAsync().ConfigureAwait(false);
                List<StatementOutcome> rows = await RunScriptAsync(embedded, "MATCH (n:Smoke {name: 'ghost'}) RETURN n.name;").ConfigureAwait(false);
                Require(rows[0].Table is { Rows.Count: 0 }, "rolled-back node is visible");
                return true;
            }).ConfigureAwait(false);

            await SamplesAsync(scope, embedded).ConfigureAwait(false);
        }

        await WireAsync(engines, StudioModel.Graph, async (scope, endPoint) =>
        {
            var wire = new GraphWorkspace(ConnectionMode.WireLoopback, engines, endPoint);
            await using (wire.ConfigureAwait(false))
            {
                if (await UseAsync(scope, wire).ConfigureAwait(false))
                {
                    await GraphRoundTripAsync(scope, wire, "Wire").ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    private async Task GraphRoundTripAsync(string scope, GraphWorkspace workspace, string tag)
    {
        await ScriptStepAsync(scope, "insert-match-rows", workspace,
            $"INSERT (a:Smoke {{name: '{tag}A', n: 1}})-[:LINKS {{w: 5}}]->(b:Smoke {{name: '{tag}B', n: 2}}); MATCH (a:Smoke {{name: '{tag}A'}})-[r:LINKS]->(b) RETURN a.name, b.name, r.w;",
            outcomes => ExpectRows(outcomes[1], 1, row => row[1] == $"{tag}B" && row[2] == "5")).ConfigureAwait(false);

        await ScriptStepAsync(scope, "match-paths", workspace,
            $"MATCH p = (a:Smoke {{name: '{tag}A'}})-[r:LINKS]->(b) RETURN p;",
            outcomes => Expect(outcomes[0].Paths is { Count: 1 } paths && paths[0].Contains($"{tag}B", StringComparison.Ordinal) && paths[0].Contains("-[", StringComparison.Ordinal),
                $"paths: {string.Join(" | ", outcomes[0].Paths ?? [])}")).ConfigureAwait(false);

        await CatalogStepAsync(scope, workspace, "Smoke").ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Key-Value

    private async Task KeyValueAsync(StudioEngines engines)
    {
        var embedded = new KeyValueWorkspace(ConnectionMode.Embedded, engines, null);
        await using (embedded.ConfigureAwait(false))
        {
            await DeclaredDatabaseAsync("keyvalue/declared", embedded).ConfigureAwait(false);

            const string scope = "keyvalue/embedded";
            if (await PrepareDatabaseAsync(scope, embedded).ConfigureAwait(false))
            {
                await KeyValueRoundTripAsync(scope, embedded).ConfigureAwait(false);

                await StepAsync(scope, "transaction-api-rollback", async () =>
                {
                    await embedded.BeginTransactionAsync(IsolationLevel.Snapshot).ConfigureAwait(false);
                    await embedded.PutAsync(Bytes.Parse("ghost", false), Bytes.Parse("x", false), KeyValueCondition.None, null).ConfigureAwait(false);
                    await embedded.RollbackAsync().ConfigureAwait(false);
                    Require(!await embedded.ExistsAsync(Bytes.Parse("ghost", false)).ConfigureAwait(false), "rolled-back key exists");
                    return true;
                }).ConfigureAwait(false);
            }
        }

        await WireAsync(engines, StudioModel.KeyValue, async (scope, endPoint) =>
        {
            var wire = new KeyValueWorkspace(ConnectionMode.WireLoopback, engines, endPoint);
            await using (wire.ConfigureAwait(false))
            {
                if (await UseAsync(scope, wire).ConfigureAwait(false))
                {
                    await KeyValueRoundTripAsync(scope, wire).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    private async Task KeyValueRoundTripAsync(string scope, KeyValueWorkspace workspace)
    {
        byte[] Key(string text) => Bytes.Parse(text, false);

        await StepAsync(scope, "put-get", async () =>
        {
            (bool applied, long? etag) = await workspace.PutAsync(Key("user:1"), Key("ada"), KeyValueCondition.None, null).ConfigureAwait(false);
            Require(applied && etag is not null, "put not applied");
            KeyValueItem? item = await workspace.GetAsync(Key("user:1")).ConfigureAwait(false);
            Require(item is { } found && found.ValueText == "ada" && found.ETag == etag, $"get returned {item?.Summary ?? "null"}");
            return true;
        }).ConfigureAwait(false);

        await StepAsync(scope, "conditional-write", async () =>
        {
            KeyValueItem current = (await workspace.GetAsync(Key("user:1")).ConfigureAwait(false))!;
            (bool stale, _) = await workspace.PutAsync(Key("user:1"), Key("nope"), KeyValueCondition.IfETagMatches, current.ETag + 1000).ConfigureAwait(false);
            Require(!stale, "stale etag write applied");
            (bool swapped, _) = await workspace.PutAsync(Key("user:1"), Key("ada-2"), KeyValueCondition.IfETagMatches, current.ETag).ConfigureAwait(false);
            Require(swapped, "matching etag write not applied");
            (bool absent, _) = await workspace.PutAsync(Key("user:1"), Key("dup"), KeyValueCondition.IfAbsent, null).ConfigureAwait(false);
            Require(!absent, "IfAbsent applied over an existing key");
            return true;
        }).ConfigureAwait(false);

        await StepAsync(scope, "scan-delete", async () =>
        {
            await workspace.PutAsync(Key("user:2"), Key("grace"), KeyValueCondition.None, null).ConfigureAwait(false);
            await workspace.PutAsync(Key("vendor:1"), Key("acme"), KeyValueCondition.None, null).ConfigureAwait(false);
            List<KeyValueItem> users = await workspace.ScanAsync(new KeyValueScan(Key("user:"), null, null, null)).ConfigureAwait(false);
            Require(users.Count == 2 && users[0].KeyText == "user:1" && users[1].KeyText == "user:2", $"prefix scan returned {users.Count}");
            List<KeyValueItem> limited = await workspace.ScanAsync(new KeyValueScan(null, Key("user:2"), null, 1)).ConfigureAwait(false);
            Require(limited.Count == 1 && limited[0].KeyText == "user:2", $"range scan returned {string.Join(",", limited.Select(i => i.KeyText))}");
            Require(await workspace.DeleteAsync(Key("vendor:1"), null).ConfigureAwait(false), "delete returned false");
            Require(!await workspace.ExistsAsync(Key("vendor:1")).ConfigureAwait(false), "deleted key still exists");
            return true;
        }).ConfigureAwait(false);

        await StepAsync(scope, "keyspaces", async () =>
        {
            TabularResult keySpaces = await workspace.KeySpacesAsync().ConfigureAwait(false);
            Require(keySpaces.Rows.Count >= 1, "KEYSPACES returned no rows");
            return keySpaces.Rows.Count;
        }).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Blob

    private async Task BlobAsync(StudioEngines engines)
    {
        var embedded = new BlobWorkspace(ConnectionMode.Embedded, engines, null);
        await using (embedded.ConfigureAwait(false))
        {
            await DeclaredDatabaseAsync("blob/declared", embedded).ConfigureAwait(false);

            const string scope = "blob/embedded";
            if (await PrepareDatabaseAsync(scope, embedded).ConfigureAwait(false))
            {
                await BlobRoundTripAsync(scope, embedded, "emb").ConfigureAwait(false);
            }
        }

        await WireAsync(engines, StudioModel.Blob, async (scope, endPoint) =>
        {
            var wire = new BlobWorkspace(ConnectionMode.WireLoopback, engines, endPoint);
            await using (wire.ConfigureAwait(false))
            {
                if (await UseAsync(scope, wire).ConfigureAwait(false))
                {
                    await BlobRoundTripAsync(scope, wire, "wire").ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    private async Task BlobRoundTripAsync(string scope, BlobWorkspace workspace, string container)
    {
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("cohesion blob smoke payload\n", 4096)));

        await StepAsync(scope, "container-create", async () =>
        {
            await workspace.CreateContainerAsync(container).ConfigureAwait(false);
            List<string> containers = await workspace.ListContainersAsync().ConfigureAwait(false);
            Require(containers.Contains(container), "container not listed");
            return true;
        }).ConfigureAwait(false);

        await StepAsync(scope, "upload-list-properties", async () =>
        {
            using var source = new MemoryStream(payload);
            await workspace.UploadAsync(container, "docs/readme.txt", source, "text/plain", overwrite: true).ConfigureAwait(false);
            List<BlobItem> blobs = await workspace.ListBlobsAsync(container, "docs/").ConfigureAwait(false);
            Require(blobs.Count == 1 && blobs[0].Properties.Length == payload.Length, $"listed {blobs.Count} blobs");
            BlobItem? properties = await workspace.GetPropertiesAsync(container, "docs/readme.txt").ConfigureAwait(false);
            Require(properties is { } found && found.Properties.ContentType == "text/plain", "properties mismatch");
            return true;
        }).ConfigureAwait(false);

        await StepAsync(scope, "download-compare", async () =>
        {
            using var destination = new MemoryStream();
            await workspace.DownloadAsync(container, "docs/readme.txt", destination).ConfigureAwait(false);
            Require(destination.ToArray().AsSpan().SequenceEqual(payload), $"downloaded {destination.Length} bytes, expected {payload.Length}");
            return true;
        }).ConfigureAwait(false);

        await StepAsync(scope, "delete-drop", async () =>
        {
            Require(await workspace.DeleteAsync(container, "docs/readme.txt").ConfigureAwait(false), "delete returned false");
            await workspace.DropContainerAsync(container).ConfigureAwait(false);
            Require(!(await workspace.ListContainersAsync().ConfigureAwait(false)).Contains(container), "container still listed");
            return true;
        }).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- declared databases and registered functions

    /// <summary>
    /// The database every engine's builder declares (<see cref="StudioEngines.DeclaredDatabase"/>)
    /// exists once the engine is built, the engine refuses to drop it, and it opens like any other.
    /// </summary>
    private async Task<bool> DeclaredDatabaseAsync(string scope, ModelWorkspace workspace)
    {
        bool declared = await StepAsync(scope, "declared-database", async () =>
        {
            IReadOnlyList<string> names = await workspace.ListDatabasesAsync(discoverOnDisk: false).ConfigureAwait(false);
            Require(names.Contains(StudioEngines.DeclaredDatabase, StringComparer.OrdinalIgnoreCase),
                $"'{StudioEngines.DeclaredDatabase}' not listed after the engine's build ({string.Join(", ", names)})");
            try
            {
                await workspace.DropDatabaseAsync(StudioEngines.DeclaredDatabase).ConfigureAwait(false);
            }
            catch (DatabaseObjectLockedException)
            {
                // The declaration owns the database: the drop is refused, and the database stays.
                return true;
            }

            throw new InvalidOperationException($"DROP of the declared database '{StudioEngines.DeclaredDatabase}' was not refused.");
        }).ConfigureAwait(false);

        return declared && await StepAsync(scope, "use-declared-database", async () =>
        {
            await workspace.UseDatabaseAsync(StudioEngines.DeclaredDatabase).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The SQL engine provisioned the declared database's typed schema at its build, and the CHECK
    /// that schema declares calls a registered function. Leaves the table empty, so a reused data
    /// root runs it again.
    /// </summary>
    private async Task DeclaredSqlSchemaAsync(string scope, SqlWorkspace workspace)
    {
        string table = StudioSqlExtensions.NotesTable;
        await ScriptStepAsync(scope, "provisioned-schema", workspace,
            $"DELETE FROM {table}; INSERT INTO {table} (Id, Title) VALUES (1, 'Ada Lovelace'); SELECT Id, {StudioSqlExtensions.InitialsFunction}(Title) FROM {table}; DELETE FROM {table};",
            outcomes => Expect(outcomes[1].AffectedCount == 1, $"insert affected {outcomes[1].AffectedCount}")
                ?? ExpectRows(outcomes[2], 1, row => row[1] == "AL")).ConfigureAwait(false);

        await ScriptStepAsync(scope, "check-calls-registered-function", workspace,
            $"INSERT INTO {table} (Id, Title) VALUES (2, '   ');",
            outcomes => Expect(outcomes[0].Failed && NamesCheck(outcomes[0]),
                $"a title with no word was not refused by {StudioSqlExtensions.NotesCheck}: {Describe(outcomes[0])}"),
            allowFailures: true).ConfigureAwait(false);

        static bool NamesCheck(StatementOutcome outcome)
            => (outcome.Error ?? string.Empty).Contains(StudioSqlExtensions.NotesCheck, StringComparison.OrdinalIgnoreCase)
                || outcome.Diagnostics.Any(diagnostic => diagnostic.Message.Contains(StudioSqlExtensions.NotesCheck, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The functions <see cref="StudioSqlExtensions"/> registers resolve like built-ins: a scalar per
    /// row, an aggregate over the table, and both listed in <c>COHESION_SCHEMA.FUNCTIONS</c>.
    /// </summary>
    private Task RegisteredFunctionsStepAsync(string scope, SqlWorkspace workspace, string table, int rows, string lastInitial, string product)
        => ScriptStepAsync(scope, "registered-functions", workspace,
            $"SELECT id, {StudioSqlExtensions.InitialsFunction}(name) FROM {table} ORDER BY id; " +
            $"SELECT {StudioSqlExtensions.ProductFunction}(id) FROM {table}; " +
            "SELECT FUNCTION_NAME FROM COHESION_SCHEMA.FUNCTIONS WHERE IS_BUILT_IN = 'NO' ORDER BY FUNCTION_NAME;",
            outcomes => ExpectRows(outcomes[0], rows, row => row[1] == lastInitial)
                ?? ExpectRows(outcomes[1], 1, row => row[0] == product)
                ?? ExpectRows(outcomes[2], 2, row => row[0] == StudioSqlExtensions.ProductFunction));

    // ---------------------------------------------------------------- shared steps

    private async Task<bool> PrepareDatabaseAsync(string scope, ModelWorkspace workspace)
    {
        IReadOnlyList<string> existing = await workspace.ListDatabasesAsync(discoverOnDisk: true).ConfigureAwait(false);
        if (existing.Contains(databaseName, StringComparer.OrdinalIgnoreCase))
        {
            await workspace.DropDatabaseAsync(databaseName).ConfigureAwait(false);
        }

        bool created = await StepAsync(scope, "create-database", async () =>
        {
            await workspace.CreateDatabaseAsync(databaseName).ConfigureAwait(false);
            IReadOnlyList<string> names = await workspace.ListDatabasesAsync(discoverOnDisk: false).ConfigureAwait(false);
            Require(names.Contains(databaseName, StringComparer.OrdinalIgnoreCase), "database not listed after create");
            return true;
        }).ConfigureAwait(false);

        return created && await UseAsync(scope, workspace).ConfigureAwait(false);
    }

    private async Task<bool> UseAsync(string scope, ModelWorkspace workspace)
        => await StepAsync(scope, "use-database", async () =>
        {
            await workspace.UseDatabaseAsync(databaseName).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    private async Task WireAsync(StudioEngines engines, StudioModel model, Func<string, EndPoint, Task> body)
    {
        string scope = $"{model.FolderName}/wire";
        IPEndPoint? endPoint = await StepAsync(scope, "start-loopback-server", () => engines.StartServerAsync(model, 0)).ConfigureAwait(false);
        if (endPoint is null)
        {
            return;
        }

        _write($"      {scope}: listening on {endPoint}");
        try
        {
            await body(scope, endPoint).ConfigureAwait(false);

            // Observation, not an assertion: how does the client report a database the server lacks?
            await StepAsync(scope, "unknown-database (observation)", async () =>
            {
                ModelWorkspace probe = StudioState.CreateWorkspace(model, ConnectionMode.WireExternal, engines, endPoint);
                await using (probe.ConfigureAwait(false))
                {
                    try
                    {
                        await probe.UseDatabaseAsync("no_such_database").ConfigureAwait(false);
                        return "connect reported NO error (handshake deferred to the first operation)";
                    }
                    catch (Exception exception)
                    {
                        // The exception text is the observation.
                        return ErrorText.Describe(exception).ReplaceLineEndings(" | ");
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await StepAsync(scope, "stop-loopback-server", async () =>
            {
                await engines.StopServerAsync(model).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
    }

    private async Task CatalogStepAsync(string scope, ModelWorkspace workspace, string expected)
    {
        await StepAsync(scope, "catalog", async () =>
        {
            List<CatalogLine> lines = workspace is LanguageWorkspace language
                ? await language.GetCatalogAsync().ConfigureAwait(false)
                : [];
            Require(lines.Any(line => line.Text.Contains(expected, StringComparison.Ordinal)), $"'{expected}' not in catalog ({lines.Count} lines)");
            return lines.Count;
        }).ConfigureAwait(false);
    }

    private async Task SamplesAsync(string scope, LanguageWorkspace workspace)
    {
        foreach (SampleScript sample in workspace.Samples)
        {
            bool expectErrors = sample.Name.Contains("expected errors", StringComparison.OrdinalIgnoreCase);
            await ScriptStepAsync($"{scope}/samples", sample.Name, workspace, sample.Text, outcomes =>
            {
                if (expectErrors)
                {
                    return Expect(outcomes.Any(o => o.Failed) && outcomes.SelectMany(o => o.Diagnostics).Any(d => d.Severity == "Error" || d.Code.Length > 0) || outcomes.Any(o => o.Error is not null),
                        "expected a failure with diagnostics");
                }

                return null;
            }, allowFailures: expectErrors).ConfigureAwait(false);
        }
    }

    private static async Task<List<StatementOutcome>> RunScriptAsync(LanguageWorkspace workspace, string script)
    {
        List<StatementOutcome> outcomes = await workspace.ExecuteScriptAsync(script, 0, script.Length, new ExecuteOptions()).ConfigureAwait(false);
        StatementOutcome? failure = outcomes.FirstOrDefault(o => o.Failed);
        if (failure is not null)
        {
            throw new InvalidOperationException(Describe(failure));
        }

        return outcomes;
    }

    private async Task ScriptStepAsync(string scope, string step, LanguageWorkspace workspace, string script, Func<List<StatementOutcome>, string?> verify, bool allowFailures = false)
    {
        await StepAsync(scope, step, async () =>
        {
            List<StatementOutcome> outcomes = await workspace.ExecuteScriptAsync(script, 0, script.Length, new ExecuteOptions(StopOnError: !allowFailures)).ConfigureAwait(false);
            if (!allowFailures && outcomes.FirstOrDefault(o => o.Failed || o.Skipped) is { } failure)
            {
                throw new InvalidOperationException(Describe(failure));
            }

            if (verify(outcomes) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            return $"{outcomes.Count} stmt";
        }).ConfigureAwait(false);
    }

    private static string Describe(StatementOutcome outcome)
    {
        var builder = new StringBuilder($"statement '{outcome.Preview}' {outcome.Status}");
        if (outcome.Error is not null)
        {
            builder.Append(": ").Append(outcome.Error.ReplaceLineEndings(" "));
        }

        foreach (DiagnosticInfo diagnostic in outcome.Diagnostics)
        {
            builder.Append($" [{diagnostic.Source} {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}]");
        }

        return builder.ToString();
    }

    private static string? Expect(bool condition, string message) => condition ? null : message;

    private static string? ExpectRows(StatementOutcome outcome, int count, Func<string[], bool> last)
    {
        if (outcome.Table is not { } table)
        {
            return "no result table";
        }

        if (table.Rows.Count != count)
        {
            return $"expected {count} rows, got {table.Rows.Count}: {string.Join(" | ", table.Rows.Select(r => string.Join(",", r)))}";
        }

        return count == 0 || last(table.Rows[^1]) ? null : $"unexpected last row: {string.Join(",", table.Rows[^1])}";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private async Task<T?> StepAsync<T>(string scope, string step, Func<Task<T>> action)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            T result = await action().ConfigureAwait(false);
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            string detail = result is bool or null ? string.Empty : $"  [{result}]";
            _passed++;
            _write($"PASS  {scope,-26} {step}  ({ms:F0} ms){detail}");
            return result;
        }
        catch (Exception exception)
        {
            // Every step failure becomes a FAIL line; the run continues with the next step.
            Fail(scope, step, ErrorText.Describe(exception).ReplaceLineEndings(" | "), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return default;
        }
    }

    private void Fail(string scope, string step, string message, double ms)
    {
        _failed++;
        _write($"FAIL  {scope,-26} {step}  ({ms:F0} ms): {message}");
    }

    private void Skip(string scope, string step, string reason)
    {
        _skipped++;
        _write($"SKIP  {scope,-26} {step}: {reason}");
    }
}
