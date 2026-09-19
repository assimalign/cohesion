using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Measures SQL profile claims against the live engine's planner, executor, and storage.
/// </summary>
public sealed class SqlLanguageConformanceTests
{
    private static readonly string[] Seed =
    [
        "CREATE TABLE t (id INT PRIMARY KEY, name TEXT, age INT);",
        "INSERT INTO t VALUES (1, 'Ada', 36), (2, 'Grace', 45), (3, 'Alan', 41);",
    ];

    /// <summary>Requires every advertised clause to have a case with correct live-engine results.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Profile: every advertised clause has a verified live execution case")]
    public async Task Profile_EveryAdvertisedClause_ShouldExecuteItsMappedCase()
    {
        // The profile drives enumeration. A newly advertised clause cannot silently escape this test.
        var cases = CreateCases();
        // Phase 21 expands ALTER TABLE's measured subset without adding a named clause.
        SqlLanguageProfile.Instance.Clauses.Count().ShouldBe(33);
        RequireExecutionCases(SqlLanguageProfile.Instance.Clauses, cases);
        cases.Keys.Except(SqlLanguageProfile.Instance.Clauses).ShouldBeEmpty("Cases must describe the current profile.");

        foreach (string clause in SqlLanguageProfile.Instance.Clauses)
        {
            var executionCase = cases[clause];
            var parsed = new SqlQueryParser().Parse(executionCase.Statement).ShouldBeOfType<SqlQueryStatement>();
            parsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error, clause);
            executionCase.ContainsClause(parsed.SqlExpression).ShouldBeTrue($"The {clause} case must actually contain that clause.");

            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-profile" });
            var database = await engine.CreateDatabaseAsync("audit");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            foreach (string setup in executionCase.Setup)
            {
                (await ExecuteAsync(session, setup)).Status.ShouldBe(QueryResultStatus.Success, $"{clause}: {setup}");
            }

            var result = await ExecuteAsync(session, executionCase.Statement);
            result.Status.ShouldBe(QueryResultStatus.Success, clause);
            await executionCase.Verify(session, result);
        }
    }

    /// <summary>Proves that an added profile entry cannot bypass the execution-case completeness guard.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Profile: adding an advertised clause without a case fails coverage")]
    public void Profile_AdditionalClauseWithoutExecutionCase_ShouldFailCoverage()
    {
        var clauses = SqlLanguageProfile.Instance.Clauses.Append("FUTURE SQL CLAUSE").ToArray();
        Should.Throw<ShouldAssertException>(() => RequireExecutionCases(clauses, CreateCases()))
            .Message.ShouldContain("FUTURE SQL CLAUSE", Case.Sensitive);
    }

    /// <summary>Restores the audit's valid conversion cases with target values and metadata.</summary>
    /// <param name="sql">The conversion to submit through the session text entry point.</param>
    /// <param name="expected">The expected value, whose runtime type differs from the operand.</param>
    /// <param name="type">The expected target metadata.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: audited valid conversions execute with target types")]
    [InlineData("SELECT CAST('42' AS INT) FROM t WHERE id = 1;", 42, DatabaseType.Int32)]
    [InlineData("SELECT CAST(42 AS TEXT) FROM t WHERE id = 1;", "42", DatabaseType.String)]
    public async Task Cast_AuditedValidConversion_ShouldReturnTargetValueAndMetadata(string sql, object expected, DatabaseType type)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-profile-cast" });
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, Seed[0]);
        await ExecuteAsync(session, Seed[1]);
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<QueryResultSet>();
        result.Columns[0].Type.ShouldBe(type);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    /// <summary>Reverses the aggregate audit exclusions with verified values and rejects ungrouped columns.</summary>
    /// <param name="projection">An aggregate projection from the original unsupported-forms audit.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SELECT: audited aggregate forms execute and ungrouped columns fail")]
    [InlineData("COUNT(age)")]
    [InlineData("SUM(age)")]
    [InlineData("AVG(age)")]
    [InlineData("MIN(age)")]
    [InlineData("MAX(age)")]
    [InlineData("COUNT(*), id")]
    [InlineData("COUNT(*) + 1")]
    public async Task Select_AuditedAggregateForms_ShouldExecuteWithGroupingValidation(string projection)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-profile-aggregate" });
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in Seed) { await ExecuteAsync(session, setup); }
        (await RowsAsync(session, "SELECT COUNT(*) FROM t WHERE age > 40;")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        string sql = $"SELECT {projection} FROM t;";
        new SqlQueryParser().Parse(sql).Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (projection == "COUNT(*), id")
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));
            error.Message.ShouldContain("id", Case.Sensitive);
            error.Message.ShouldContain("GROUP BY", Case.Sensitive);
            return;
        }
        object expected = projection switch
        {
            "COUNT(age)" => 3L,
            "SUM(age)" => 122m,
            "AVG(age)" => 122m / 3m,
            "MIN(age)" => 36,
            "MAX(age)" => 45,
            "COUNT(*) + 1" => 4L,
            _ => throw new InvalidOperationException($"Missing expected value for {projection}."),
        };
        (await RowsAsync(session, sql)).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    private static void RequireExecutionCases(IEnumerable<string> clauses, IReadOnlyDictionary<string, ExecutionCase> cases)
        => clauses.Where(clause => !cases.ContainsKey(clause)).ShouldBeEmpty("Every advertised clause needs a live execution case.");

    /// <summary>Reverses the default audit failures while retaining the unsupported ordering observations.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Partial clauses: defaults backfill or reject atomically and ORDER BY limits remain explicit")]
    public async Task PartialForms_DefaultsAndOrdering_ShouldMatchMeasuredBoundaries()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-profile-partial" });
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in Seed) { await ExecuteAsync(session, setup); }
        // #1023: the parsed expression must fail before publishing a column or changing old rows.
        string alter = "ALTER TABLE t ADD COLUMN extra INT DEFAULT (1 + 2);";
        new SqlQueryParser().Parse(alter).Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, alter)))
            .Message.ShouldBe("Column 'extra': only literal DEFAULT values are supported.");
        await ExpectRowsAsync(session, "SELECT * FROM t ORDER BY id;", [[1, "Ada", 36], [2, "Grace", 45], [3, "Alan", 41]]);
        await ExecuteAsync(session, "INSERT INTO t (id, name, age) VALUES (4, 'new', 1);");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT extra FROM t;")))
            .Message.ShouldBe("Unknown column 'extra'.");
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN literal_default INT DEFAULT 7;");
        await ExpectRowsAsync(session, "SELECT id, name, age, literal_default FROM t ORDER BY id;",
            [[1, "Ada", 36, 7], [2, "Grace", 45, 7], [3, "Alan", 41, 7], [4, "new", 1, 7]]);
        await ExecuteAsync(session, "INSERT INTO t (id, name, age) VALUES (5, 'next', 2);");
        await ExpectRowsAsync(session, "SELECT literal_default FROM t WHERE id = 5;", [[7]]);
        // #1024 must bind projection aliases and ordinals before these forms count as correct execution.
        var aliasError = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT age AS years FROM t ORDER BY years;"));
        aliasError.Message.ShouldBe("Unknown column 'years'.");
        await ExpectRowsAsync(session, "SELECT id FROM t ORDER BY 1 DESC;", [[1], [2], [3], [4], [5]]);
    }

    private static Dictionary<string, ExecutionCase> CreateCases() => new(StringComparer.Ordinal)
    {
        [SqlClauses.Select] = Query("SELECT id, age + 1 AS next_age FROM t ORDER BY id;", expression => expression is SqlSelectExpression,
            [[1, 37L], [2, 46L], [3, 42L]]),
        [SqlClauses.From] = Query("SELECT u.name FROM dbo.t AS u WHERE id = 1;", expression => expression is SqlSelectExpression { From: not null },
            [["Ada"]]),
        [SqlClauses.Subquery] = Query("SELECT id, (SELECT MAX(age) FROM t) AS oldest FROM t WHERE id IN (SELECT id FROM t WHERE age > 40) AND EXISTS (SELECT id FROM t WHERE age = 36) ORDER BY id;",
            expression => expression is SqlSelectExpression select && select.Columns.Any(column => column.Expression is SqlSubqueryExpression),
            [[2, 45], [3, 45]]),
        [SqlClauses.Join] = new("SELECT t.name, p.email FROM t INNER JOIN profiles p ON t.id = p.user_id ORDER BY t.id;",
            [.. Seed, "CREATE TABLE profiles (user_id INT REFERENCES t(id), email TEXT);",
                "INSERT INTO profiles VALUES (1, 'ada@example.test'), (3, 'alan@example.test');"],
            expression => expression is SqlSelectExpression { Joins.Count: 1 } select && select.Joins[0].JoinType == SqlJoinType.Inner,
            async (_, result) => CheckRows(await ReadRowsAsync(result), [["Ada", "ada@example.test"], ["Alan", "alan@example.test"]])),
        [SqlClauses.Where] = Query("SELECT id FROM t WHERE age > 40 AND name LIKE 'G%' ORDER BY id;", expression => expression is SqlSelectExpression { Where: not null },
            [[2]]),
        [SqlClauses.Collate] = Query("SELECT id FROM t WHERE name = 'ada' COLLATE case_insensitive;",
            expression => expression is SqlSelectExpression { Where: SqlBinaryExpression { Right: SqlCollateExpression } }, [[1]]),
        [SqlClauses.GroupBy] = Query("SELECT age > 40, COUNT(*), SUM(age) FROM t GROUP BY age > 40 ORDER BY age > 40;",
            expression => expression is SqlSelectExpression { GroupBy.Count: 1 }, [[false, 1L, 36m], [true, 2L, 86m]]),
        [SqlClauses.Having] = Query("SELECT age > 40, COUNT(*), SUM(age) FROM t WHERE age > 35 GROUP BY age > 40 HAVING SUM(age) > 50;",
            expression => expression is SqlSelectExpression { Having: not null }, [[true, 2L, 86m]]),
        [SqlClauses.OrderBy] = Query("SELECT id FROM t ORDER BY age DESC, id ASC;", expression => expression is SqlSelectExpression { OrderBy.Count: 2 },
            [[2], [3], [1]]),
        [SqlClauses.Limit] = Query("SELECT id FROM t ORDER BY id LIMIT 2;", expression => expression is SqlSelectExpression { Limit: not null },
            [[1], [2]]),
        [SqlClauses.Offset] = Query("SELECT id FROM t ORDER BY id OFFSET 1;", expression => expression is SqlSelectExpression { Offset: not null },
            [[2], [3]]),
        [SqlClauses.Case] = Query("SELECT CASE WHEN age > 40 THEN 'senior' ELSE 'junior' END, CASE id WHEN 1 THEN 'first' ELSE 'later' END FROM t ORDER BY id;",
            expression => expression is SqlSelectExpression select && select.Columns.All(column => column.Expression is SqlCaseExpression),
            [["junior", "first"], ["senior", "later"], ["senior", "later"]]),
        [SqlClauses.Cast] = new("SELECT CAST('42' AS INT), CAST(age AS TEXT) FROM t ORDER BY id;", Seed,
            expression => expression is SqlSelectExpression select && select.Columns.All(column => column.Expression is SqlCastExpression),
            async (_, result) =>
            {
                var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();
                resultSet.Columns[0].Type.ShouldBe(DatabaseType.Int32);
                resultSet.Columns[1].Type.ShouldBe(DatabaseType.String);
                CheckRows(await ReadRowsAsync(resultSet), [[42, "36"], [42, "45"], [42, "41"]]);
            }),
        [SqlClauses.Insert] = new("INSERT INTO t (id, name, age) VALUES (4, 'Barbara', 33);", Seed,
            expression => expression is SqlInsertExpression,
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(1);
                await ExpectRowsAsync(session, "SELECT name, age FROM t WHERE id = 4;", [["Barbara", 33]]);
            }),
        [SqlClauses.Values] = new("INSERT INTO t VALUES (4, 'Barbara', 33), (5, 'Edsger', 42);", Seed,
            expression => expression is SqlInsertExpression { Values.Count: 2 },
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(2);
                await ExpectRowsAsync(session, "SELECT name FROM t WHERE id > 3 ORDER BY id;", [["Barbara"], ["Edsger"]]);
            }),
        [SqlClauses.Update] = new("UPDATE t SET age = age + 10 WHERE id = 1;", Seed,
            expression => expression is SqlUpdateExpression,
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(1);
                await ExpectRowsAsync(session, "SELECT age FROM t ORDER BY id;", [[46], [45], [41]]);
            }),
        [SqlClauses.Delete] = new("DELETE FROM t WHERE id = 2;", Seed,
            expression => expression is SqlDeleteExpression,
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(1);
                await ExpectRowsAsync(session, "SELECT id FROM t ORDER BY id;", [[1], [3]]);
            }),
        [SqlClauses.CreateTable] = new("CREATE TABLE created (id INT PRIMARY KEY, label TEXT DEFAULT 'new');", [],
            expression => expression is SqlCreateTableExpression,
            async (session, _) =>
            {
                await ExecuteAsync(session, "INSERT INTO created (id) VALUES (7);");
                await ExpectRowsAsync(session, "SELECT id, label FROM created;", [[7, "new"]]);
            }),
        [SqlClauses.AlterTable] = new("ALTER TABLE t ADD COLUMN extra INT NOT NULL DEFAULT 7;", Seed,
            expression => expression is SqlAlterTableExpression { Action: SqlAlterAddColumnAction }, VerifyAlterAsync),
        [SqlClauses.DropTable] = new("DROP TABLE t;", Seed,
            expression => expression is SqlDropTableExpression,
            async (session, _) =>
            {
                ((SqlDatabaseInstance)session.Database).Catalog.TryGetTable("dbo", "t", out var dropped).ShouldBeFalse();
                dropped.ShouldBeNull();
                (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT * FROM t;")))
                    .Message.ShouldContain("does not exist", Case.Sensitive);
                (await ExecuteAsync(session, "DROP TABLE IF EXISTS t;")).Status.ShouldBe(QueryResultStatus.Success);
            }),
        [SqlClauses.CreateIndex] = new("CREATE UNIQUE INDEX ix_name ON t(name);", Seed,
            expression => expression is SqlCreateIndexExpression { IsUnique: true },
            async (session, _) =>
            {
                var catalog = ((SqlDatabaseInstance)session.Database).Catalog;
                catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
                catalog.GetIndexes(table!.ObjectId).ShouldContain(index => index.Name == "ix_name" && index.IsUnique);
                await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO t VALUES (4, 'Ada', 1);"));
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE name = 'Ada';", [[1]]);
            }),
        [SqlClauses.DropIndex] = new("DROP INDEX ix_age ON t;", [.. Seed, "CREATE INDEX ix_age ON t(age);"],
            expression => expression is SqlDropIndexExpression,
            async (session, _) =>
            {
                var catalog = ((SqlDatabaseInstance)session.Database).Catalog;
                catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
                catalog.GetIndexes(table!.ObjectId).ShouldNotContain(index => index.Name == "ix_age");
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE age = 36;", [[1]]);
                (await ExecuteAsync(session, "DROP INDEX IF EXISTS ix_age ON t;")).Status.ShouldBe(QueryResultStatus.Success);
            }),
        [SqlClauses.Constraint] = new("CREATE TABLE c (code INT, CONSTRAINT uq_code UNIQUE(code));", [],
            expression => expression is SqlCreateTableExpression create && create.Constraints.Any(constraint => constraint.Name == "uq_code"), VerifyUniqueAsync),
        [SqlClauses.UniqueConstraint] = new("CREATE TABLE c (code INT UNIQUE);", [],
            expression => HasConstraint(expression, SqlConstraintKind.Unique), VerifyUniqueAsync),
        [SqlClauses.Check] = new("CREATE TABLE c (code INT CHECK(code > 0));", [],
            expression => HasConstraint(expression, SqlConstraintKind.Check),
            async (session, _) =>
            {
                await ExecuteAsync(session, "INSERT INTO c VALUES (1), (NULL);");
                var error = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (0);"));
                error.ConstraintKind.ShouldBe("CHECK");
                await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c;", [[2L]]);
            }),
        [SqlClauses.ForeignKey] = ForeignKeyCase("CREATE TABLE c (pid INT, CONSTRAINT fk_parent FOREIGN KEY(pid) REFERENCES t(id));", SqlReferentialAction.Restrict, VerifyReferenceAsync),
        [SqlClauses.References] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id));", SqlReferentialAction.Restrict, VerifyReferenceAsync),
        [SqlClauses.Cascade] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id) ON DELETE CASCADE);", SqlReferentialAction.Cascade,
            async (session, result) =>
            {
                await VerifyReferenceAsync(session, result);
                await ExecuteAsync(session, "DELETE FROM t WHERE id = 1;");
                await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c WHERE pid = 1;", [[0L]]);
                await ExpectRowsAsync(session, "SELECT pid FROM c;", [[null]]);
                await ExpectRowsAsync(session, "SELECT COUNT(*) FROM t;", [[2L]]);
            }),
        [SqlClauses.Restrict] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id) ON DELETE RESTRICT);", SqlReferentialAction.Restrict,
            async (session, result) =>
            {
                await VerifyReferenceAsync(session, result);
                var error = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "DELETE FROM t WHERE id = 1;"));
                error.ConstraintKind.ShouldBe("FOREIGN KEY");
                await ExpectRowsAsync(session, "SELECT pid FROM c WHERE pid = 1;", [[1]]);
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE id = 1;", [[1]]);
            }),
        [SqlClauses.Begin] = new("BEGIN;", Seed,
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Begin }, VerifyBeginAsync),
        [SqlClauses.Commit] = new("COMMIT;", [.. Seed, "BEGIN;", "INSERT INTO t VALUES (4, 'new', 1);"],
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Commit },
            async (session, _) =>
            {
                session.CurrentTransaction.ShouldBeNull();
                await using var observer = await session.Database.CreateSessionAsync();
                await ExpectRowsAsync(observer, "SELECT id FROM t WHERE id = 4;", [[4]]);
            }),
        [SqlClauses.Rollback] = new("ROLLBACK;", [.. Seed, "BEGIN;", "INSERT INTO t VALUES (4, 'new', 1);", "DELETE FROM t WHERE id = 2;"],
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Rollback },
            async (session, _) =>
            {
                session.CurrentTransaction.ShouldBeNull();
                await using var observer = await session.Database.CreateSessionAsync();
                await ExpectRowsAsync(observer, "SELECT id FROM t ORDER BY id;", [[1], [2], [3]]);
            }),
        [SqlClauses.Transaction] = new("BEGIN TRANSACTION;", Seed,
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Begin } && expression.Text!.Contains("TRANSACTION", StringComparison.Ordinal),
            async (session, _) =>
            {
                session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Active);
                await ExecuteAsync(session, "INSERT INTO t VALUES (4, 'new', 1);");
                await ExecuteAsync(session, "COMMIT TRANSACTION;");
                await ExecuteAsync(session, "BEGIN TRANSACTION;");
                await ExecuteAsync(session, "DELETE FROM t WHERE id = 4;");
                await ExecuteAsync(session, "ROLLBACK TRANSACTION;");
                session.CurrentTransaction.ShouldBeNull();
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE id = 4;", [[4]]);
            }),
    };

    private static ExecutionCase Query(string statement, Func<SqlQueryExpression, bool> containsClause, object?[][] expected)
        => new(statement, Seed, containsClause, async (_, result) => CheckRows(await ReadRowsAsync(result), expected));

    private static ExecutionCase ForeignKeyCase(string statement, SqlReferentialAction action, Func<IDatabaseSession, QueryResult, Task> verify)
        => new(statement, Seed, expression => expression is SqlCreateTableExpression create &&
            create.Constraints.Any(constraint => constraint.Kind == SqlConstraintKind.ForeignKey && constraint.ReferencedTable is not null && constraint.OnDelete == action), verify);

    private static bool HasConstraint(SqlQueryExpression expression, SqlConstraintKind kind)
        => expression is SqlCreateTableExpression create && create.Constraints.Any(constraint => constraint.Kind == kind);

    private static async Task VerifyAlterAsync(IDatabaseSession session, QueryResult result)
    {
        result.Status.ShouldBe(QueryResultStatus.Success);
        await ExpectRowsAsync(session, "SELECT id, name, age, extra FROM t ORDER BY id;",
            [[1, "Ada", 36, 7], [2, "Grace", 45, 7], [3, "Alan", 41, 7]]);
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN nullable_extra INT;");
        await ExpectRowsAsync(session, "SELECT nullable_extra FROM t ORDER BY id;", [[null], [null], [null]]);
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN nullable_extra;");
        await ExecuteAsync(session, "INSERT INTO t (id, name, age) VALUES (4, 'new', 1);");
        await ExpectRowsAsync(session, "SELECT id, name, age, extra FROM t WHERE id = 4;", [[4, "new", 1, 7]]);
        await ExecuteAsync(session, "ALTER TABLE t ADD CONSTRAINT positive CHECK(extra > 0);");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "UPDATE t SET extra = 0 WHERE id = 1;")))
            .ConstraintKind.ShouldBe("CHECK");
        await ExecuteAsync(session, "ALTER TABLE t DROP CONSTRAINT positive;");
        await ExecuteAsync(session, "UPDATE t SET extra = 0 WHERE id = 1;");
        await ExpectRowsAsync(session, "SELECT extra FROM t WHERE id = 1;", [[0]]);
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN extra;");
        await ExpectRowsAsync(session, "SELECT * FROM t WHERE id = 1;", [[1, "Ada", 36]]);
    }

    private static async Task VerifyUniqueAsync(IDatabaseSession session, QueryResult result)
    {
        result.Status.ShouldBe(QueryResultStatus.Success);
        await ExecuteAsync(session, "INSERT INTO c VALUES (1), (NULL);");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (1);")))
            .ConstraintKind.ShouldBe("UNIQUE");
        // This dialect's unique index treats NULL as a key, so a second NULL is also a duplicate.
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (NULL);")))
            .ConstraintKind.ShouldBe("UNIQUE");
        await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c;", [[2L]]);
    }

    private static async Task VerifyReferenceAsync(IDatabaseSession session, QueryResult result)
    {
        result.Status.ShouldBe(QueryResultStatus.Success);
        await ExecuteAsync(session, "INSERT INTO c VALUES (1), (NULL);");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (99);")))
            .ConstraintKind.ShouldBe("FOREIGN KEY");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "UPDATE t SET id = 99 WHERE id = 1;")))
            .ConstraintKind.ShouldBe("FOREIGN KEY");
        await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c;", [[2L]]);
    }

    private static async Task VerifyBeginAsync(IDatabaseSession session, QueryResult result)
    {
        result.Status.ShouldBe(QueryResultStatus.Success);
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Active);
        await ExecuteAsync(session, "INSERT INTO t VALUES (4, 'new', 1);");
        await using var observer = await session.Database.CreateSessionAsync();
        await ExpectRowsAsync(observer, "SELECT COUNT(*) FROM t;", [[3L]]);
        await ExpectRowsAsync(session, "SELECT COUNT(*) FROM t;", [[4L]]);
        await ExecuteAsync(session, "ROLLBACK;");
        await ExpectRowsAsync(observer, "SELECT COUNT(*) FROM t;", [[3L]]);
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement)
        => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask();

    private static async Task ExpectRowsAsync(IDatabaseSession session, string statement, object?[][] expected)
        => CheckRows(await RowsAsync(session, statement), expected);

    private static void CheckRows(List<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++) { actual[i].ShouldBe(expected[i]); }
    }

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string statement)
        => await ReadRowsAsync(await ExecuteAsync(session, statement));

    private static async Task<List<object?[]>> ReadRowsAsync(QueryResult queryResult)
    {
        await using var result = queryResult.ShouldBeAssignableTo<QueryResultSet>();
        result.Status.ShouldBe(QueryResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    private sealed record ExecutionCase(
        string Statement,
        string[] Setup,
        Func<SqlQueryExpression, bool> ContainsClause,
        Func<IDatabaseSession, QueryResult, Task> Verify);
}
