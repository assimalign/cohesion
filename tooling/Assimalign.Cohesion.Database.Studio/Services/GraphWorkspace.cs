using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Client;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Graph (GQL): embedded session or <c>Graph.Client</c>, with row and path dispatch.</summary>
internal sealed partial class GraphWorkspace : LanguageWorkspace
{
    private GraphClient? _client;
    private GraphConnection? _connection;
    private string? _wireDatabase;

    public GraphWorkspace(ConnectionMode mode, StudioEngines engines, EndPoint? wireEndPoint)
        : base(StudioModel.Graph, mode, engines, wireEndPoint)
    {
    }

    public override string LanguageName => "GQL";

    protected override string ClientName => "Graph.Client GraphConnection";

    public override bool SupportsGraphResultMode => true;

    public override IReadOnlyList<SampleScript> Samples => GraphSamples.All;

    protected override QueryStatement ParseLocally(string statement) => new GqlQueryParser().Parse(statement);

    protected override async Task OpenWireAsync(string database, EndPoint endPoint, CancellationToken cancellationToken)
    {
        _client = GraphClient.Create(new GraphClientOptions
        {
            Settings = WireSettings(database, endPoint),
            ConnectionFactory = new TcpConnectionFactory(),
        });
        _wireDatabase = database;
        _connection = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask CloseWireAsync()
    {
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_client is { } client)
        {
            _client = null;
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Path execution needs a read-only MATCH that returns exactly one bare variable.</summary>
    public static bool LooksLikePathQuery(string statement)
    {
        if (ScriptSplitter.FirstKeyword(statement) != "MATCH")
        {
            return false;
        }

        Match match = ReturnClause().Match(statement);
        return match.Success && !MutationVerb().IsMatch(statement);
    }

    [GeneratedRegex(@"\bRETURN\s+[A-Za-z_][A-Za-z0-9_]*(\s+AS\s+[A-Za-z_][A-Za-z0-9_]*)?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReturnClause();

    [GeneratedRegex(@"\b(INSERT|CREATE|DELETE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MutationVerb();

    protected override async Task ExecuteStatementAsync(StatementOutcome outcome, ExecuteOptions options, CancellationToken cancellationToken)
    {
        bool paths = options.GraphMode switch
        {
            GraphResultMode.Paths => true,
            GraphResultMode.Rows => false,
            _ => LooksLikePathQuery(outcome.Statement),
        };

        if (Mode == ConnectionMode.Embedded)
        {
            DatabaseSession session = RequireSession();
            if (paths)
            {
                QueryResult result = await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(outcome.Statement), cancellationToken).ConfigureAwait(false);
                outcome.Status = result.Status.ToString();
                outcome.Failed = result.Status != QueryResultStatus.Success;
                outcome.AffectedCount = result.AffectedCount;
                if (result is GraphPathsQueryResult pathsResult)
                {
                    SetPaths(outcome, pathsResult.Paths);
                }
                else
                {
                    await FillFromQueryResultAsync(outcome, result, cancellationToken).ConfigureAwait(false);
                }

                outcome.Note = "DatabaseSession.ExecuteAsync(GraphPathsQueryRequest)";
                return;
            }

            QueryResult rows = await session.ExecuteAsync(outcome.Statement, null, cancellationToken).ConfigureAwait(false);
            await FillFromQueryResultAsync(outcome, rows, cancellationToken).ConfigureAwait(false);
            return;
        }

        GraphConnection connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (paths)
        {
            var list = new List<GraphPath>();
            await foreach (GraphPath path in connection.QueryPathsAsync(outcome.Statement, null, cancellationToken).ConfigureAwait(false))
            {
                list.Add(path);
            }

            SetPaths(outcome, list);
            outcome.Note = "GraphConnection.QueryPathsAsync (ExecutePaths exchange)";
            return;
        }

        GraphResultSet set = await connection.QueryAsync(outcome.Statement, null, cancellationToken).ConfigureAwait(false);
        outcome.AffectedCount = set.AffectedCount;
        if (set.Columns.Count > 0)
        {
            outcome.Table = ToTable(set.Columns.Select(column => (column.Name, column.Type.ToString())), set.Select(row => (IReadOnlyList<object?>)row.ToArray()));
        }

        outcome.Note = "GraphConnection.QueryAsync (Execute exchange)";
    }

    private static void SetPaths(StatementOutcome outcome, IReadOnlyList<GraphPath> paths)
    {
        outcome.Paths = [.. paths.Select(ValueFormatter.FormatPath)];
        var table = new TabularResult { Columns = ["#", "path"], ColumnTypes = ["Int32", "Path"] };
        for (int i = 0; i < outcome.Paths.Count && i < StatementOutcome.MaxRows; i++)
        {
            table.Rows.Add([(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.Paths[i]]);
        }

        table.Truncated = outcome.Paths.Count > StatementOutcome.MaxRows;
        outcome.Table = table;
    }

    private async Task<GraphConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await CloseWireAsync().ConfigureAwait(false);
        await OpenWireAsync(_wireDatabase ?? RequireDatabase(), WireEndPoint!, cancellationToken).ConfigureAwait(false);
        return _connection!;
    }

    protected override async Task<List<CatalogLine>> BuildCatalogAsync(CancellationToken cancellationToken)
    {
        var lines = new List<CatalogLine>();

        TabularResult labels = await QueryTableAsync("SHOW LABELS", cancellationToken).ConfigureAwait(false);
        lines.Add(new CatalogLine($"Labels ({labels.Rows.Count})", IsHeader: true, InsertText: "SHOW LABELS;"));
        foreach (string[] row in labels.Rows)
        {
            string name = labels.Get(row, "LABEL_NAME");
            lines.Add(new CatalogLine($"{name}  ({labels.Get(row, "LABEL_ID")})", 1, $"MATCH (n:{name}) RETURN n;"));
        }

        TabularResult types = await QueryTableAsync("SHOW RELATIONSHIP TYPES", cancellationToken).ConfigureAwait(false);
        lines.Add(new CatalogLine($"Relationship types ({types.Rows.Count})", IsHeader: true, InsertText: "SHOW RELATIONSHIP TYPES;"));
        foreach (string[] row in types.Rows)
        {
            string name = types.Get(row, "RELATIONSHIP_TYPE_NAME");
            lines.Add(new CatalogLine(name, 1, $"MATCH p = (a)-[r:{name}]->(b) RETURN p;"));
        }

        TabularResult keys = await QueryTableAsync("SHOW PROPERTY KEYS", cancellationToken).ConfigureAwait(false);
        lines.Add(new CatalogLine($"Property keys ({keys.Rows.Count})", IsHeader: true, InsertText: "SHOW PROPERTY KEYS;"));
        foreach (string[] row in keys.Rows)
        {
            string type = keys.Get(row, "DATA_TYPE");
            lines.Add(new CatalogLine(
                $"{keys.Get(row, "DEFINITION_TYPE")} {keys.Get(row, "DEFINITION_NAME")}.{keys.Get(row, "PROPERTY_KEY")}  {(type == ValueFormatter.NullText ? "(any)" : type)}{(keys.Get(row, "IS_REQUIRED") == "true" ? " required" : string.Empty)}",
                1));
        }

        TabularResult indexes = await QueryTableAsync("SHOW INDEXES", cancellationToken).ConfigureAwait(false);
        lines.Add(new CatalogLine($"Indexes ({indexes.Rows.Count})", IsHeader: true, InsertText: "SHOW INDEXES;"));
        foreach (string[] row in indexes.Rows)
        {
            lines.Add(new CatalogLine(
                $"{indexes.Get(row, "LABEL_NAME")}.{indexes.Get(row, "INDEX_NAME")} ({indexes.Get(row, "PROPERTY_KEY")}){(indexes.Get(row, "IS_UNIQUE") == "true" ? " UNIQUE" : string.Empty)}",
                1));
        }

        TabularResult ownership = await QueryTableAsync("SHOW OBJECT OWNERSHIP", cancellationToken).ConfigureAwait(false);
        lines.Add(new CatalogLine($"Object ownership ({ownership.Rows.Count})", IsHeader: true, InsertText: "SHOW OBJECT OWNERSHIP;"));
        foreach (string[] row in ownership.Rows)
        {
            string schema = ownership.Get(row, "OWNING_SCHEMA");
            lines.Add(new CatalogLine(
                $"{ownership.Get(row, "OBJECT_TYPE")} {ownership.Get(row, "OBJECT_NAME")}: {ownership.Get(row, "OWNER")}{(schema is ("" or ValueFormatter.NullText) ? string.Empty : $" ({schema})")}",
                1));
        }

        return lines;
    }
}

internal static class GraphSamples
{
    public static IReadOnlyList<SampleScript> All { get; } =
    [
        new("1. Insert a small graph", """
            INSERT (a:Person {name: 'Alice', age: 42, active: TRUE})-[:KNOWS {since: 2019}]->(b:Person {name: 'Bob', age: 17, active: FALSE});
            INSERT (:Person {name: 'Cara', age: 30});
            MATCH (a:Person {name: 'Bob'}), (c:Person {name: 'Cara'}) INSERT (a)-[:KNOWS {since: 2023}]->(c);
            """),
        new("2. Match scalar rows", """
            MATCH (a:Person)-[r:KNOWS]->(b) RETURN a.name, b.name AS friend, r.since;
            MATCH (a:Person) WHERE a.age >= 18 AND a.active = TRUE RETURN a.name AS adult, a.age;
            MATCH (b:Person {name: 'Bob'})<-[r:KNOWS]-(a) RETURN a.name;
            """),
        new("3. Paths and entities (ExecutePaths)", """
            MATCH p = (a:Person {name: 'Alice'})-[r:KNOWS]->(b)-[s:KNOWS]->(c) RETURN p;
            MATCH (n:Person) RETURN n;
            MATCH (a)-[r:KNOWS]->(b) RETURN r;
            """),
        new("4. Catalog (SHOW)", """
            SHOW LABELS;
            SHOW RELATIONSHIP TYPES;
            SHOW PROPERTY KEYS;
            SHOW INDEXES;
            SHOW OBJECT OWNERSHIP;
            """),
        new("5. Diagnostics demo (expected errors)", """
            MATCH (a) RETURN count(a);
            """),
        new("6. Cleanup", """
            MATCH (a:Person) DETACH DELETE a;
            """),
    ];
}
