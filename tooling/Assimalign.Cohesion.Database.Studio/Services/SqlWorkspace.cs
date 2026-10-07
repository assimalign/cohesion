using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>SQL: embedded <c>DatabaseSession</c> or the typed <c>Sql.Client</c> over TCP.</summary>
internal sealed class SqlWorkspace : LanguageWorkspace
{
    private SqlClient? _client;
    private SqlConnection? _connection;
    private string? _wireDatabase;
    private bool _wireTransaction;

    public SqlWorkspace(ConnectionMode mode, StudioEngines engines, EndPoint? wireEndPoint)
        : base(StudioModel.Sql, mode, engines, wireEndPoint)
    {
    }

    public override string LanguageName => "SQL";

    protected override string ClientName => "Sql.Client SqlConnection";

    /// <summary>SQL supports BEGIN/COMMIT/ROLLBACK statements, so the buttons also work over the wire.</summary>
    public override bool SupportsSessionTransactions => true;

    public override string? TransactionText => Mode == ConnectionMode.Embedded
        ? base.TransactionText
        : _wireTransaction ? "BEGIN sent over the wire (tracked by the Studio)" : null;

    public override IReadOnlyList<SampleScript> Samples => SqlSamples.All;

    protected override QueryStatement ParseLocally(string statement) => new SqlQueryParser().Parse(statement);

    protected override async Task OpenWireAsync(string database, EndPoint endPoint, CancellationToken cancellationToken)
    {
        _client = SqlClient.Create(new SqlClientOptions
        {
            Settings = WireSettings(database, endPoint),
            ConnectionFactory = new TcpConnectionFactory(),
        });
        _wireDatabase = database;
        _connection = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _wireTransaction = false;
    }

    protected override async ValueTask CloseWireAsync()
    {
        _wireTransaction = false;
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

    protected override Task BeginWireTransactionAsync(CancellationToken cancellationToken)
        => SendControlAsync("BEGIN;", cancellationToken);

    protected override Task EndWireTransactionAsync(bool commit, CancellationToken cancellationToken)
        => SendControlAsync(commit ? "COMMIT;" : "ROLLBACK;", cancellationToken);

    private async Task SendControlAsync(string statement, CancellationToken cancellationToken)
    {
        var outcome = new StatementOutcome { Statement = statement };
        await ExecuteStatementAsync(outcome, new ExecuteOptions(), cancellationToken).ConfigureAwait(false);
        if (outcome.Failed)
        {
            throw new InvalidOperationException(outcome.Error ?? string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }
    }

    protected override async Task ExecuteStatementAsync(StatementOutcome outcome, ExecuteOptions options, CancellationToken cancellationToken)
    {
        if (Mode == ConnectionMode.Embedded)
        {
            QueryResult result = await RequireSession().ExecuteAsync(outcome.Statement, null, cancellationToken).ConfigureAwait(false);
            await FillFromQueryResultAsync(outcome, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        SqlConnection connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        string keyword = ScriptSplitter.FirstKeyword(outcome.Statement);

        try
        {
            // The typed client splits rows (QueryAsync) from affected counts (ExecuteAsync); the
            // consumer has to pick one per statement before sending it.
            if (keyword == "SELECT")
            {
                SqlResultSet set = await connection.QueryAsync(outcome.Statement, null, cancellationToken).ConfigureAwait(false);
                outcome.Table = ToTable(
                    set.Columns.Select(column => (column.Name, column.Type.ToString())),
                    set.Select(row => (IReadOnlyList<object?>)Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()));
                outcome.Note = "SqlConnection.QueryAsync (the typed client exposes no affected count here)";
            }
            else
            {
                outcome.AffectedCount = await connection.ExecuteAsync(outcome.Statement, null, cancellationToken).ConfigureAwait(false);
                outcome.Note = "SqlConnection.ExecuteAsync (the typed client returns no rows here)";
                _wireTransaction = keyword switch
                {
                    "BEGIN" => true,
                    "COMMIT" or "ROLLBACK" => false,
                    _ => _wireTransaction,
                };
            }
        }
        catch (SqlClientException exception) when (!exception.ConnectionUsable)
        {
            // Drop the broken connection; the next statement reconnects.
            await CloseWireAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SqlConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
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

        TabularResult tables = await QueryTableAsync(
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME;", cancellationToken).ConfigureAwait(false);
        TabularResult columns = await QueryTableAsync(
            "SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION;", cancellationToken).ConfigureAwait(false);
        TabularResult indexes = await QueryTableAsync(
            "SELECT TABLE_SCHEMA, TABLE_NAME, INDEX_NAME, COLUMN_NAME, IS_UNIQUE, IS_PRIMARY_KEY FROM COHESION_SCHEMA.INDEXES ORDER BY TABLE_SCHEMA, TABLE_NAME, INDEX_NAME, ORDINAL_POSITION;", cancellationToken).ConfigureAwait(false);
        TabularResult constraints = await QueryTableAsync(
            "SELECT TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_NAME, CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS ORDER BY TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_NAME;", cancellationToken).ConfigureAwait(false);
        TabularResult ownership = await QueryTableAsync(
            "SELECT TABLE_SCHEMA, TABLE_NAME, OBJECT_TYPE, OBJECT_NAME, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY TABLE_SCHEMA, TABLE_NAME, OBJECT_TYPE, OBJECT_NAME;", cancellationToken).ConfigureAwait(false);

        lines.Add(new CatalogLine($"Tables ({tables.Rows.Count})", IsHeader: true));
        foreach (string[] table in tables.Rows)
        {
            string schema = tables.Get(table, "TABLE_SCHEMA");
            string name = tables.Get(table, "TABLE_NAME");
            string qualified = Qualify(schema, name);
            bool Match(TabularResult source, string[] row) =>
                source.Get(row, "TABLE_SCHEMA") == schema && source.Get(row, "TABLE_NAME") == name;

            lines.Add(new CatalogLine(qualified, 1, $"SELECT * FROM {qualified} LIMIT 100;"));

            lines.Add(new CatalogLine("columns", 2));
            foreach (string[] column in columns.Rows.Where(row => Match(columns, row)))
            {
                string type = columns.Get(column, "DATA_TYPE");
                string length = columns.Get(column, "CHARACTER_MAXIMUM_LENGTH");
                string precision = columns.Get(column, "NUMERIC_PRECISION");
                string scale = columns.Get(column, "NUMERIC_SCALE");
                if (length is not ("" or ValueFormatter.NullText))
                {
                    type += $"({length})";
                }
                else if (precision is not ("" or ValueFormatter.NullText) && type == "NUMERIC")
                {
                    type += scale is ("" or ValueFormatter.NullText) ? $"({precision})" : $"({precision},{scale})";
                }

                string nullable = columns.Get(column, "IS_NULLABLE") == "YES" ? "NULL" : "NOT NULL";
                string defaultValue = columns.Get(column, "COLUMN_DEFAULT");
                string suffix = defaultValue is ("" or ValueFormatter.NullText) ? string.Empty : $" DEFAULT {defaultValue}";
                lines.Add(new CatalogLine($"{columns.Get(column, "COLUMN_NAME")}  {type}  {nullable}{suffix}", 3, columns.Get(column, "COLUMN_NAME")));
            }

            var tableIndexes = indexes.Rows.Where(row => Match(indexes, row))
                .GroupBy(row => indexes.Get(row, "INDEX_NAME"))
                .ToList();
            if (tableIndexes.Count > 0)
            {
                lines.Add(new CatalogLine("indexes", 2));
                foreach (var index in tableIndexes)
                {
                    string[] first = index.First();
                    string flags = (indexes.Get(first, "IS_PRIMARY_KEY") == "YES" ? " PRIMARY KEY" : string.Empty)
                        + (indexes.Get(first, "IS_UNIQUE") == "YES" ? " UNIQUE" : string.Empty);
                    string keyColumns = string.Join(", ", index.Select(row => indexes.Get(row, "COLUMN_NAME")));
                    lines.Add(new CatalogLine($"{index.Key} ({keyColumns}){flags}", 3));
                }
            }

            var tableConstraints = constraints.Rows.Where(row => Match(constraints, row)).ToList();
            if (tableConstraints.Count > 0)
            {
                lines.Add(new CatalogLine("constraints", 2));
                foreach (string[] constraint in tableConstraints)
                {
                    lines.Add(new CatalogLine($"{constraints.Get(constraint, "CONSTRAINT_NAME")}  {constraints.Get(constraint, "CONSTRAINT_TYPE")}", 3));
                }
            }

            foreach (string[] owner in ownership.Rows.Where(row => Match(ownership, row) && ownership.Get(row, "OBJECT_TYPE") == "TABLE"))
            {
                string owningSchema = ownership.Get(owner, "OWNING_SCHEMA");
                lines.Add(new CatalogLine(
                    $"owner: {ownership.Get(owner, "OWNER")}{(owningSchema is ("" or ValueFormatter.NullText) ? string.Empty : $" (schema {owningSchema})")}", 2));
            }
        }

        lines.Add(new CatalogLine("System relations", IsHeader: true));
        foreach (string relation in _systemRelations)
        {
            lines.Add(new CatalogLine(relation, 1, $"SELECT * FROM {relation};"));
        }

        return lines;
    }

    private static readonly string[] _systemRelations =
    [
        "INFORMATION_SCHEMA.TABLES",
        "INFORMATION_SCHEMA.COLUMNS",
        "INFORMATION_SCHEMA.TABLE_CONSTRAINTS",
        "INFORMATION_SCHEMA.KEY_COLUMN_USAGE",
        "INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS",
        "INFORMATION_SCHEMA.CHECK_CONSTRAINTS",
        "COHESION_SCHEMA.INDEXES",
        "COHESION_SCHEMA.OBJECT_OWNERSHIP",
    ];

    private static string Qualify(string schema, string name)
        => schema is "" or ValueFormatter.NullText ? name : $"{schema}.{name}";
}

internal static class SqlSamples
{
    public static IReadOnlyList<SampleScript> All { get; } =
    [
        new("1. Create tables + index", """
            CREATE TABLE IF NOT EXISTS customers (
                id BIGINT PRIMARY KEY,
                name TEXT NOT NULL,
                city TEXT COLLATE case_insensitive
            );
            CREATE TABLE IF NOT EXISTS orders (
                id BIGINT PRIMARY KEY,
                customer_id BIGINT NOT NULL REFERENCES customers (id),
                item VARCHAR(40) NOT NULL,
                amount DECIMAL(10,2) DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_orders_customer ON orders (customer_id);
            """),
        new("2. Insert rows", InsertRowsScript()),
        new("3. Query: filter, join, group", """
            SELECT id, name, city FROM customers WHERE city = 'LONDON' ORDER BY id;
            SELECT c.name, o.item, o.amount FROM customers AS c INNER JOIN orders AS o ON c.id = o.customer_id ORDER BY o.amount DESC;
            SELECT c.name, COUNT(*) AS orders, SUM(o.amount) AS total FROM customers AS c INNER JOIN orders AS o ON c.id = o.customer_id GROUP BY c.name HAVING COUNT(*) >= 1 ORDER BY total DESC;
            SELECT name FROM customers WHERE id IN (SELECT customer_id FROM orders WHERE amount > 50) ORDER BY name;
            """),
        new("4. Update / delete in a transaction", """
            BEGIN;
            UPDATE customers SET city = 'Paris' WHERE id = 1;
            SELECT id, name, city FROM customers WHERE id = 1;
            ROLLBACK;
            SELECT id, name, city FROM customers WHERE id = 1;
            """),
        new("5. Catalog (INFORMATION_SCHEMA / COHESION_SCHEMA)", """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME;
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_NAME, ORDINAL_POSITION;
            SELECT TABLE_NAME, INDEX_NAME, COLUMN_NAME, IS_UNIQUE, IS_PRIMARY_KEY FROM COHESION_SCHEMA.INDEXES ORDER BY TABLE_NAME, INDEX_NAME;
            SELECT TABLE_NAME, OBJECT_TYPE, OBJECT_NAME, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP;
            """),
        new("6. Diagnostics demo (expected errors)", """
            SELECT name FROM customers UNION SELECT item FROM orders;
            """),
        new("7. Cleanup", """
            DROP TABLE IF EXISTS orders;
            DROP TABLE IF EXISTS customers;
            """),
    ];

    // Sample 2 inserts 1,000 customers and 1,000 orders, one multi-row INSERT per table. The rows
    // are generated so this file stays readable; the script the editor shows is plain SQL. The
    // original rows keep their ids and values (customers 1-3, orders 10-13), so samples 3 and 4
    // still find Ada in London and update customer 1. Lower-case 'london' rows exercise the
    // city column's case_insensitive collation in sample 3's WHERE city = 'LONDON'.
    private static string InsertRowsScript()
    {
        const int rowCount = 1_000;
        string[] firstNames = ["Ada", "Grace", "Linus", "Barbara", "Edsger", "Margaret", "Alan", "Frances", "Donald", "Radia"];
        string[] lastNames = ["Lovelace", "Hopper", "Torvalds", "Liskov", "Dijkstra", "Hamilton", "Turing", "Allen", "Knuth", "Perlman"];
        string[] cities = ["London", "Arlington", "Helsinki", "Paris", "Berlin", "Tokyo", "london", "Oslo"];
        string[] items = ["engine", "gears", "compiler", "kernel", "parser", "router", "sensor", "cable"];

        var script = new StringBuilder();
        script.Append("INSERT INTO customers (id, name, city) VALUES\n");
        script.Append("    (1, 'Ada', 'London'),\n    (2, 'Grace', 'Arlington'),\n    (3, 'Linus', 'Helsinki')");
        for (int id = 4; id <= rowCount; id++)
        {
            string name = $"{firstNames[id % firstNames.Length]} {lastNames[id / firstNames.Length % lastNames.Length]}";
            script.Append(CultureInfo.InvariantCulture, $",\n    ({id}, '{name}', '{cities[id % cities.Length]}')");
        }

        script.Append(";\n\nINSERT INTO orders (id, customer_id, item, amount) VALUES\n");
        script.Append("    (10, 1, 'engine', 120.50),\n    (11, 1, 'gears', 15.25),\n    (12, 2, 'compiler', 99.99),\n    (13, 3, 'kernel', 42.00)");
        for (int id = 14; id < 10 + rowCount; id++)
        {
            int customerId = id * 7 % rowCount + 1;
            decimal amount = id * 1237 % 100_000 / 100m;
            script.Append(CultureInfo.InvariantCulture, $",\n    ({id}, {customerId}, '{items[id % items.Length]}', {amount:0.00})");
        }

        script.Append(";\n");
        return script.ToString();
    }
}
