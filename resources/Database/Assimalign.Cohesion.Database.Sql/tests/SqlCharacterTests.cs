using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>CHAR semantics at the session and client boundaries, before and after file-backed reopen.</summary>
public sealed class SqlCharacterTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CHAR: public typed row accessors expose padded strings and retain conversions")]
    public async Task GetValue_CharacterColumn_ShouldExposePaddedStringsAndConversions()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("character-accessors", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE codes (c CHAR(5))");
        await session.ExecuteAsync("INSERT INTO codes VALUES ('12')");
        // Act
        await using var result = (await session.ExecuteAsync("SELECT c FROM codes")).ShouldBeAssignableTo<DatabaseResultSet>();
        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            count++;
            row.GetValue(0).ShouldBe("12   ");
            row.GetString(0).ShouldBe("12   ");
            row.GetInt32(0).ShouldBe(12);
        }
        count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CHAR: padding, keys, functions and metadata survive the wire and reopen")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CharacterValues_ShouldKeepPaddingAndComparisonIdentity(bool wire)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-character", Guid.NewGuid().ToString("N"));
        try
        {
            for (int opening = 0; opening < 2; opening++)
            {
                await using var engine = SqlDatabaseEngine.Create("character", new SqlDatabaseEngineOptions { RootPath = root });
                var database = opening == 0 ? await engine.CreateDatabaseAsync("chars") : await engine.OpenDatabaseAsync("chars");
                await using var session = await database.CreateSessionAsync();
                await using var listener = new InMemoryConnectionListener();
                await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
                if (wire) { await server.StartAsync(); }
                await using var client = SqlClient.Create(new SqlClientOptions
                {
                    Settings = new DatabaseConnectionSettings { Database = "chars", Principal = "character-tests", EndPoint = listener.EndPoint },
                    ConnectionFactory = listener.CreateFactory(),
                });
                await using var connection = wire ? await client.ConnectAsync(TestTimeout.Token()) : null;

                async Task Execute(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
                {
                    if (connection is not null) { await connection.ExecuteAsync(sql, parameters, TestTimeout.Token()); }
                    else { (await session.ExecuteAsync(sql, parameters, CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success); }
                }

                async Task<(List<(DatabaseType Type, int? Length)> Columns, List<object?[]> Rows)> Query(string sql)
                {
                    if (connection is not null)
                    {
                        var result = await connection.QueryAsync(sql, cancellationToken: TestTimeout.Token());
                        return (result.Columns.Select(column => (column.Type, column.MaxLength)).ToList(),
                            result.Select(row => Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()).ToList());
                    }
                    await using var local = (await session.ExecuteAsync(sql)).ShouldBeAssignableTo<DatabaseResultSet>();
                    var rows = new List<object?[]>();
                    await foreach (var row in local.GetRowsAsync(CancellationToken.None))
                    {
                        rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
                    }
                    return (local.Columns.Select(column => (column.Type, column.MaxLength)).ToList(), rows);
                }

                async Task Reject(string sql, string code)
                {
                    if (wire) { (await Should.ThrowAsync<SqlClientException>(() => Execute(sql))).Message.ShouldContain(code); }
                    else { (await Should.ThrowAsync<DatabaseException>(() => Execute(sql))).Message.ShouldContain(code); }
                }

                if (opening == 0)
                {
                    await Execute("CREATE TABLE chars (id INT, c CHAR(3), v VARCHAR(3), bare CHAR)");
                    await Execute("INSERT INTO chars VALUES (1, 'a', 'a ', 'x'), (2, 'b  ', 'b', 'y'), (3, 'a    ', 'a', 'z')");
                    await Execute("INSERT INTO chars VALUES (4, @c, 'q', 'q')", new Dictionary<string, object?> { ["c"] = "q" });
                    await Execute("CREATE INDEX ix_chars ON chars(c)");
                    await Execute("CREATE TABLE parents (c CHAR(3) COLLATE case_insensitive PRIMARY KEY)");
                    await Execute("CREATE TABLE children (c CHAR(5) COLLATE case_insensitive REFERENCES parents(c))");
                    await Execute("INSERT INTO parents VALUES ('A')");
                    await Execute("INSERT INTO children VALUES ('a  ')");
                    await Execute("CREATE TABLE unique_chars (c CHAR(3) UNIQUE)");
                    await Execute("INSERT INTO unique_chars VALUES ('a  ')");
                    await Execute("CREATE DOMAIN code AS CHAR(3) CHECK (LENGTH(VALUE) = 1)");
                    await Execute("CREATE TABLE domains (c code DEFAULT 'd')");
                    await Execute("INSERT INTO domains VALUES ('a')");
                    await Execute("CREATE FUNCTION pad(p CHAR(3)) RETURNS CHAR(5) IMMUTABLE RETURN p");
                    await Execute("CREATE FUNCTION domain_pad(p code) RETURNS code IMMUTABLE RETURN p");
                    await Execute("CREATE FUNCTION param_like(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN p LIKE 'a  '");
                }

                // Act / Assert: stored output, modifiers and their metadata are identical on both paths.
                var stored = await Query("SELECT c, v, bare FROM chars WHERE id = 1");
                stored.Columns.ShouldBe([(DatabaseType.Character, (int?)3), (DatabaseType.String, (int?)3), (DatabaseType.Character, (int?)1)]);
                stored.Rows.ShouldBe([["a  ", "a ", "x"]]);
                (await Query("SELECT c FROM chars WHERE id = 4")).Rows.ShouldBe([["q  "]]);
                (await Query("SELECT CAST(c AS TEXT), CAST(c AS VARCHAR(5)), LENGTH(c), c || 'b', LOWER(c), UPPER(c), c LIKE 'a', c LIKE 'a  ', c LIKE 'a%' FROM chars WHERE id = 1"))
                    .Rows.ShouldBe([["a", "a", 1L, "ab", "a", "A", false, true, true]]);
                (await Query("SELECT CAST('abcdef' AS CHAR(3)), CAST('a' AS CHAR(5)), CAST('😀' AS CHAR(3)), LENGTH(CAST('😀' AS CHAR(3))) FROM chars WHERE id = 1"))
                    .Rows.ShouldBe([["abc", "a    ", "😀  ", 1L]]);
                (await Query("SELECT c = 'a ', c <> 'a', c < 'b', c = CAST('a ' AS VARCHAR(3)), c IN ('a '), c BETWEEN 'a ' AND 'b' FROM chars WHERE id = 1"))
                    .Rows.ShouldBe([[true, false, true, false, true, true]]);
                (await Query("SELECT id FROM chars WHERE c = 'a' ORDER BY id")).Rows.ShouldBe([[1], [3]]);
                if (!wire) { session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_chars"); }
                (await Query("SELECT id FROM chars WHERE c = 'a     ' ORDER BY id")).Rows.ShouldBe([[1], [3]]);
                (await Query("SELECT id FROM chars WHERE c >= 'a' AND c < 'q' ORDER BY c, id")).Rows.ShouldBe([[1], [3], [2]]);
                (await Query("SELECT c, COUNT(*) FROM chars GROUP BY c ORDER BY c")).Rows.ShouldBe([["a  ", 2L], ["b  ", 1L], ["q  ", 1L]]);
                (await Query("SELECT DISTINCT c FROM chars ORDER BY c")).Rows.ShouldBe([["a  "], ["b  "], ["q  "]]);
                (await Query("SELECT LENGTH(c), COUNT(*) FROM chars GROUP BY c ORDER BY c")).Rows.ShouldBe([[1L, 2L], [1L, 1L], [1L, 1L]]);
                (await Query("SELECT LENGTH(MIN(c)), MIN(c) || 'x', MAX(c) FROM chars")).Rows.ShouldBe([[1L, "ax", "q  "]]);
                (await Query("SELECT c LIKE CAST('a' AS CHAR(3)), LENGTH((SELECT c FROM chars WHERE id = 1)) FROM chars WHERE id = 1"))
                    .Rows.ShouldBe([[false, 1L]]);
                // PostgreSQL parse_expr.c:1668-1669 forces a simple CASE literal input to TEXT.
                (await Query("SELECT CASE 'a ' WHEN c THEN TRUE ELSE FALSE END, CASE c WHEN 'a ' THEN TRUE ELSE FALSE END, CASE CAST('a ' AS CHAR(5)) WHEN c THEN TRUE ELSE FALSE END FROM chars WHERE id = 1"))
                    .Rows.ShouldBe([[false, true, true]]);
                (await Query("SELECT CASE WHEN id = 1 THEN c ELSE CAST('x ' AS VARCHAR(3)) END, COALESCE(c, CAST('x ' AS TEXT)) FROM chars WHERE id = 1"))
                    .Rows.ShouldBe([["a", "a"]]);
                (await Query("SELECT 'a ' IN (c), 'a ' IN (SELECT c FROM chars), 'a ' BETWEEN CAST('a' AS CHAR(3)) AND CAST('b' AS CHAR(3)) FROM chars WHERE id = 1"))
                    .Rows.ShouldBe([[true, true, true]]);
                const string reversedPadding = "CASE WHEN id = 1 THEN CAST('a' AS CHAR(5)) WHEN id = 2 THEN CAST('a' AS CHAR(2)) ELSE CAST('b' AS CHAR(3)) END";
                (await Query($"SELECT id FROM chars WHERE id < 4 ORDER BY {reversedPadding}")).Rows.ShouldBe([[1], [2], [3]]);
                const string mixed = "CASE WHEN id = 1 THEN CAST('a' AS CHAR(2)) WHEN id = 2 THEN CAST('a' AS CHAR(5)) ELSE CAST('b' AS CHAR(3)) END";
                (await Query($"SELECT {mixed}, COUNT(*) FROM chars WHERE id < 4 GROUP BY {mixed} ORDER BY {mixed}"))
                    .Rows.ShouldBe([["a ", 2L], ["b  ", 1L]]);
                (await Query($"SELECT DISTINCT {mixed} FROM chars WHERE id < 4 ORDER BY {mixed}")).Rows.ShouldBe([["a "], ["b  "]]);
                (await Query("SELECT p.c, ch.c FROM parents p JOIN children ch ON p.c = ch.c")).Rows.ShouldBe([["A  ", "a    "]]);
                (await Query("SELECT c FROM parents WHERE c = 'a    '")).Rows.ShouldBe([["A  "]]);
                await Reject("INSERT INTO parents VALUES ('a ')", "duplicate key");
                await Reject("INSERT INTO unique_chars VALUES ('a')", "duplicate key");
                await Reject("INSERT INTO children VALUES ('z')", "FOREIGN KEY");
                await Reject("INSERT INTO chars VALUES (5, 'abcd', 'x', 'x')", "COHSQLE012");
                await Reject("SELECT pad('abcd') FROM chars WHERE id = 1", "COHSQLE012");
                await Reject("INSERT INTO domains VALUES ('ab')", "domain");
                var functions = await Query("SELECT pad('a'), domain_pad('a'), param_like('a'), c FROM domains");
                functions.Columns.ShouldBe([(DatabaseType.Character, (int?)5), (DatabaseType.Character, (int?)3), (DatabaseType.Boolean, (int?)null), (DatabaseType.Character, (int?)3)]);
                functions.Rows.ShouldBe([["a    ", "a  ", true, "a  "]]);
                (await Query("SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'chars' AND COLUMN_NAME = 'c'"))
                    .Rows.ShouldBe([["CHARACTER", 3L]]);
                (await Query("SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'chars' AND COLUMN_NAME = 'v'"))
                    .Rows.ShouldBe([["CHARACTER VARYING", 3L]]);
                (await Query("SELECT BASE_TYPE FROM COHESION_SCHEMA.DOMAINS WHERE DOMAIN_NAME = 'code'")).Rows.ShouldBe([["CHAR(3)"]]);
                (await Query("SELECT PARAMETER_TYPES, RETURN_TYPE FROM COHESION_SCHEMA.FUNCTIONS WHERE FUNCTION_NAME = 'pad'"))
                    .Rows.ShouldBe([["CHAR(3)", "CHAR(5)"]]);
                (await Query("SELECT TYPE_NAME FROM COHESION_SCHEMA.TYPES WHERE TYPE_NAME = 'CHAR(1)'"))
                    .Rows.ShouldBe([["CHAR(1)"]]);
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }
}
