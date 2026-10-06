using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Production persistence of SQL expression definitions (#1068 follow-up): CHECK and DEFAULT are
/// stored as canonical text rendered from the parsed tree, parsed and bound once per table
/// version, never on the write path, and a stored definition that does not load fails the open
/// with an error naming its table instead of failing later writes.
/// </summary>
public sealed class SqlPersistedDefinitionTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-persisted-definitions", Guid.NewGuid().ToString("N"));

    /// <summary>The catalog keeps the canonical predicate, not the spelling, comments or layout it was declared with.</summary>
    /// <param name="ddl">A statement declaring the check <c>ck</c> on <c>t</c>.</param>
    /// <param name="canonical">The text the catalog stores.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: CHECK is stored as canonical text")]
    [InlineData("CREATE TABLE t (qty INT, name TEXT, CONSTRAINT ck CHECK (QTY>0   and /* upper */ qty<100))", "QTY > 0 AND qty < 100")]
    [InlineData("CREATE TABLE t (qty INT CONSTRAINT ck CHECK ((qty) BETWEEN +1 AND (10)), name TEXT)", "qty BETWEEN 1 AND 10")]
    [InlineData("CREATE TABLE t (qty INT, name TEXT, CONSTRAINT ck CHECK (name <> 'O''Brien' OR \"qty\" IS NULL))", "name <> 'O''Brien' OR qty IS NULL")]
    [InlineData("CREATE TABLE t (qty INT, name TEXT, CONSTRAINT ck CHECK (+qty >= - -1))", "+qty >= -(-1)")]
    [InlineData("CREATE TABLE t (qty INT, name TEXT, CONSTRAINT ck CHECK (UPPER(name) COLLATE CASE_INSENSITIVE IN ('A','B')))",
        "UPPER(name) COLLATE case_insensitive IN ('A', 'B')")]
    public async Task Check_Declared_ShouldPersistCanonicalText(string ddl, string canonical)
    {
        // Arrange
        await using var engine = CreateEngine("persisted-canonical");
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await session.ExecuteAsync(ddl);

        // Assert
        Check(database, "t", "ck").CheckExpression.ShouldBe(canonical);
        (await RowsAsync(session, "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_NAME = 'ck'"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { canonical });
    }

    /// <summary>ALTER TABLE paths store canonical text too, and the stored predicate is what is enforced.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: ADD CONSTRAINT and ADD COLUMN store canonical CHECK text")]
    public async Task Check_AddedByAlter_ShouldPersistCanonicalText()
    {
        // Arrange
        await using var engine = CreateEngine("persisted-alter");
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (qty INT)");

        // Act
        await session.ExecuteAsync("ALTER TABLE t ADD CONSTRAINT ck_qty CHECK(qty>=0)");
        await session.ExecuteAsync("ALTER TABLE t ADD COLUMN extra INT CONSTRAINT ck_extra CHECK(extra<>-1)");

        // Assert
        Check(database, "t", "ck_qty").CheckExpression.ShouldBe("qty >= 0");
        Check(database, "t", "ck_extra").CheckExpression.ShouldBe("extra <> -1");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("INSERT INTO t VALUES (-1, 1)"));
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("INSERT INTO t VALUES (1, -1)"));
        (await session.ExecuteAsync("INSERT INTO t VALUES (1, 1)")).AffectedCount.ShouldBe(1);
    }

    /// <summary>
    /// Literal DEFAULTs are stored as canonical SQL literals — quoted, signs folded, keywords upper
    /// case — and still resolve the same values for omitted INSERT columns and after a restart.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: DEFAULT is stored as a canonical literal and survives restart")]
    public async Task Default_Declared_ShouldPersistCanonicalLiteralAndSurviveRestart()
    {
        // Arrange
        const string ddl = "CREATE TABLE d (id INT, label VARCHAR(20) DEFAULT 'it''s', amount INT DEFAULT +5, " +
            "flag BOOLEAN DEFAULT true, price DECIMAL(5, 2) DEFAULT 1.50, note TEXT DEFAULT NULL)";
        string?[] expected = [null, "'it''s'", "5", "TRUE", "1.50", null];
        object?[] values = [1, "it's", 5, true, 1.50m, null];

        await using (var engine = CreateEngine("persisted-default", _rootPath))
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);

            // Act
            await session.ExecuteAsync(ddl);
            await session.ExecuteAsync("INSERT INTO d (id) VALUES (1)");

            // Assert
            Table(database, "d").Columns.Select(column => column.DefaultLiteral).ShouldBe(expected);
            (await RowsAsync(session, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'd' ORDER BY ORDINAL_POSITION"))
                .Select(row => row[0]).ShouldBe(expected);
            (await RowsAsync(session, "SELECT * FROM d")).ShouldHaveSingleItem().ShouldBe(values);
        }

        await using (var reopenedEngine = CreateEngine("persisted-default", _rootPath))
        {
            var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("db");
            await using var session = await reopened.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO d (id) VALUES (2)");
            (await RowsAsync(session, "SELECT * FROM d WHERE id = 2")).ShouldHaveSingleItem()
                .ShouldBe(new object?[] { 2 }.Concat(values.Skip(1)).ToArray());
        }
    }

    /// <summary>
    /// Persisted definitions are bound when the table version is produced; validated inserts,
    /// updates and failing writes evaluate the cached predicate and parse nothing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: validated writes never parse persisted text")]
    public async Task Check_ValidatedWrites_ShouldNotParsePersistedText()
    {
        // Arrange
        await using var engine = CreateEngine("persisted-parse-once");
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, qty INT CHECK (qty > 0), label TEXT DEFAULT 'x')");
        long bindings = database.Definitions.BindCount;

        // Act
        for (int id = 1; id <= 50; id++)
        {
            await session.ExecuteAsync($"INSERT INTO t (id, qty) VALUES ({id}, {id})");
        }
        await session.ExecuteAsync("UPDATE t SET qty = qty + 1");
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("UPDATE t SET qty = 0 WHERE id = 1"));
        await session.ExecuteAsync("INSERT INTO t (id, qty) SELECT id + 100, qty FROM t WHERE id <= 10");
        await session.ExecuteAsync("DELETE FROM t WHERE id > 100");

        // Assert
        database.Definitions.BindCount.ShouldBe(bindings);
        (await RowsAsync(session, "SELECT COUNT(*), MIN(qty) FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 50L, 2 });
    }

    /// <summary>
    /// Every DDL that changes a table produces a new bound version, so a write is checked against
    /// exactly the constraints the table has now — never a dropped one, never without an added one.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: ADD/DROP CONSTRAINT, ADD/DROP COLUMN and DROP/CREATE TABLE rebind")]
    public async Task Check_SchemaChanges_ShouldRebindTheTableVersion()
    {
        // Arrange
        await using var engine = CreateEngine("persisted-invalidate");
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, qty INT, other INT)");

        // Act / Assert: ADD CONSTRAINT applies to the next write.
        await DdlAsync(database, session, "ALTER TABLE t ADD CONSTRAINT positive CHECK (qty > 0)");
        await WriteFailsAsync(database, session, "INSERT INTO t VALUES (1, -1, 0)");

        // DROP CONSTRAINT removes it from the next write.
        await DdlAsync(database, session, "ALTER TABLE t DROP CONSTRAINT positive");
        await WriteAsync(database, session, "INSERT INTO t VALUES (1, -1, 0)");

        // A table-level check still holds after a column it does not read is added and dropped.
        await WriteAsync(database, session, "DELETE FROM t");
        await DdlAsync(database, session, "ALTER TABLE t ADD CONSTRAINT bounded CHECK (qty < 10)");
        await DdlAsync(database, session, "ALTER TABLE t ADD COLUMN extra INT DEFAULT 3");
        await WriteFailsAsync(database, session, "INSERT INTO t (id, qty) VALUES (2, 11)");
        await DdlAsync(database, session, "ALTER TABLE t DROP COLUMN other");
        await WriteFailsAsync(database, session, "INSERT INTO t (id, qty) VALUES (2, 11)");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER TABLE t DROP COLUMN qty"));
        await WriteAsync(database, session, "INSERT INTO t (id, qty) VALUES (2, 9)");
        (await RowsAsync(session, "SELECT id, qty, extra FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 9, 3 });

        // DROP TABLE then CREATE TABLE of the same name binds the new definition only.
        await session.ExecuteAsync("DROP TABLE t");
        await DdlAsync(database, session, "CREATE TABLE t (id INT, qty INT CHECK (qty < 0))");
        await WriteFailsAsync(database, session, "INSERT INTO t VALUES (3, 11)");
        await WriteAsync(database, session, "INSERT INTO t VALUES (3, -11)");
    }

    /// <summary>
    /// Runs DDL that changes <c>t</c> and proves the statement published a new table version and
    /// bound it before returning, so the next write finds it bound.
    /// </summary>
    private static async Task DdlAsync(SqlDatabaseInstance database, IDatabaseSession session, string sql)
    {
        database.Catalog.TryGetTable("dbo", "t", out var before);
        await session.ExecuteAsync(sql);
        var after = Table(database, "t");
        after.ShouldNotBeSameAs(before, sql);
        database.Definitions.IsBound(after).ShouldBeTrue(sql);
    }

    /// <summary>Runs a write that succeeds and proves it parsed no persisted definition.</summary>
    private static async Task WriteAsync(SqlDatabaseInstance database, IDatabaseSession session, string sql)
    {
        long before = database.Definitions.BindCount;
        await session.ExecuteAsync(sql);
        database.Definitions.BindCount.ShouldBe(before, sql);
    }

    /// <summary>Runs a write a CHECK rejects and proves it parsed no persisted definition.</summary>
    private static async Task WriteFailsAsync(SqlDatabaseInstance database, IDatabaseSession session, string sql)
    {
        long before = database.Definitions.BindCount;
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync(sql));
        database.Definitions.BindCount.ShouldBe(before, sql);
    }

    /// <summary>A canonical CHECK, including a unary plus, survives restart, and writes after reopen bind nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: the open binds every table once and writes after it parse nothing")]
    public async Task Check_Reopened_ShouldBeBoundAtOpenAndEnforced()
    {
        // Arrange
        await using (var engine = CreateEngine("persisted-reopen", _rootPath))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE t (qty INT, CONSTRAINT ck CHECK (+qty BETWEEN 1 AND 9 OR qty IS NULL))");
            await session.ExecuteAsync("CREATE TABLE u (id INT, label TEXT DEFAULT 'kept')");
        }

        // Act
        await using var reopenedEngine = CreateEngine("persisted-reopen", _rootPath);
        var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("db");
        await using var reopenedSession = await reopened.CreateSessionAsync(CancellationToken.None);

        // Assert: the open bound both tables, and every write after it reuses those bindings.
        reopened.Definitions.BindCount.ShouldBe(2);
        Check(reopened, "t", "ck").CheckExpression.ShouldBe("+qty BETWEEN 1 AND 9 OR qty IS NULL");
        await WriteFailsAsync(reopened, reopenedSession, "INSERT INTO t VALUES (10)");
        await WriteAsync(reopened, reopenedSession, "INSERT INTO t VALUES (5), (NULL)");
        await WriteAsync(reopened, reopenedSession, "INSERT INTO u (id) SELECT qty FROM t WHERE qty IS NOT NULL");
        (await RowsAsync(reopenedSession, "SELECT id, label FROM u")).ShouldHaveSingleItem().ShouldBe(new object?[] { 5, "kept" });
        reopened.Definitions.BindCount.ShouldBe(2);
    }

    /// <summary>
    /// A stored definition that does not load fails the open, naming the database, table and
    /// constraint or column, rather than a later write to its table.
    /// </summary>
    /// <param name="damage">Which definition to damage.</param>
    /// <param name="named">The text the open error must contain.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: an unloadable definition fails the open with an actionable error")]
    [InlineData("unparseable-check", "CHECK constraint 'ck_qty' on table 'dbo.orders'")]
    [InlineData("check-with-trailing-clause", "CHECK constraint 'ck_qty' on table 'dbo.orders'")]
    [InlineData("unbound-check", "CHECK constraint 'ck_qty' on table 'dbo.orders'")]
    [InlineData("non-literal-default", "DEFAULT of column 'note' on table 'dbo.orders'")]
    public async Task Open_UnloadableDefinition_ShouldFailTheOpenNamingTheTable(string damage, string named)
    {
        // Arrange: a healthy database, then a damaged definition written straight to the catalog.
        await using (var engine = CreateEngine("persisted-damaged", _rootPath))
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("shop");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE orders (id INT, qty INT, note TEXT DEFAULT 'n', CONSTRAINT ck_qty CHECK (qty > 0))");
            await session.ExecuteAsync("CREATE TABLE healthy (id INT CHECK (id > 0))");

            var table = Table(database, "orders");
            var columns = table.Columns.ToArray();
            var constraints = table.Constraints.ToArray();
            switch (damage)
            {
                case "unparseable-check":
                    constraints[0] = new SqlCatalogConstraint("ck_qty", SqlCatalogConstraintKind.Check, [], checkExpression: "qty >");
                    break;
                case "check-with-trailing-clause":
                    constraints[0] = new SqlCatalogConstraint("ck_qty", SqlCatalogConstraintKind.Check, [], checkExpression: "qty > 0 ORDER BY qty");
                    break;
                case "unbound-check":
                    constraints[0] = new SqlCatalogConstraint("ck_qty", SqlCatalogConstraintKind.Check, [], checkExpression: "missing > 0");
                    break;
                default:
                    columns[2] = new SqlCatalogColumn("note", columns[2].Type, columns[2].IsNullable, "n");
                    break;
            }

            var damaged = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, table.PrimaryKeyColumns,
                table.Owner, table.OwningSchema, constraints);
            await database.Catalog.PublishTableAsync(damaged, [], database.Catalog.GetIndexRegistrations(), replaceExisting: true);
        }

        // Act
        await using var reopenedEngine = CreateEngine("persisted-damaged", _rootPath);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopenedEngine.OpenDatabaseAsync("shop"));

        // Assert
        failure.Message.ShouldStartWith("Database 'shop' cannot be opened.", Case.Sensitive);
        failure.Message.ShouldContain(named, Case.Sensitive);
        failure.Message.ShouldContain("cannot be loaded", Case.Sensitive);
        failure.Message.ShouldContain("restore the database from a backup", Case.Sensitive);
    }

    /// <summary>
    /// The CHECK validator visits every node once, so a long AND chain is declared, bound again at
    /// open, and enforced in linear time. It used to walk each AND/OR operand twice, doubling the
    /// work per term: 24 terms took seconds to open and 40 never finished declaring. An AND chain
    /// is one n-ary node of any length (#1151), so 400 comparisons, more than the default nesting
    /// limit of 256 levels, are three levels deep, and the canonical text the catalog stores for
    /// them reads back to the same chain. 400 is about as many as fit: the table's definition,
    /// its CHECK text included, must fit in one 8,092-byte catalog record.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: a long AND chain declares, opens and enforces in linear time")]
    public async Task Check_LongConjunction_ShouldDeclareOpenAndEnforceInLinearTime()
    {
        // Arrange
        const int terms = 400;
        string predicate = string.Join(" AND ", Enumerable.Range(1, terms).Select(term => $"qty <> {term.ToString(CultureInfo.InvariantCulture)}"));
        var elapsed = Stopwatch.StartNew();

        // Act
        await using (var engine = CreateEngine("persisted-long-check", _rootPath))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync($"CREATE TABLE t (qty INT, CONSTRAINT ck CHECK ({predicate}))");
        }

        await using var reopenedEngine = CreateEngine("persisted-long-check", _rootPath);
        var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("db");
        await using var reopenedSession = await reopened.CreateSessionAsync(CancellationToken.None);
        await WriteFailsAsync(reopened, reopenedSession, $"INSERT INTO t VALUES ({terms.ToString(CultureInfo.InvariantCulture)})");
        await WriteAsync(reopened, reopenedSession, $"INSERT INTO t VALUES ({(terms + 1).ToString(CultureInfo.InvariantCulture)})");
        elapsed.Stop();

        // Assert: linear work finishes in milliseconds; the bound only absorbs a slow agent.
        reopened.Definitions.BindCount.ShouldBe(1);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Opening binds a stored CHECK — columns, collations, a Boolean row predicate the evaluator
    /// can run — without re-applying the rules DDL uses to accept one. A predicate an earlier
    /// engine accepted but today's DDL would refuse (a sign over a TEXT column, a CAST) still
    /// opens and is enforced, and an unrelated DROP COLUMN still succeeds, so tightening a
    /// declaration rule can never lock a database out.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: open binds stored CHECKs without re-applying declaration rules")]
    public async Task Open_CheckOutsideTodaysDeclarationRules_ShouldOpenAndEnforce()
    {
        // Arrange: predicates the current DDL rejects, written straight to the catalog.
        await using (var engine = CreateEngine("persisted-rules", _rootPath))
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE t (id INT, label TEXT, qty INT, extra INT)");
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER TABLE t ADD CONSTRAINT signed CHECK (-label IS NULL)")))
                .Message.ShouldStartWith("COHSQLE003", Case.Sensitive);
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER TABLE t ADD CONSTRAINT via_cast CHECK (CAST(qty AS VARCHAR(5)) <> '13')")))
                .Message.ShouldContain("casts", Case.Sensitive);

            var table = Table(database, "t");
            var stored = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, table.Columns.ToArray(), table.PrimaryKeyColumns,
                table.Owner, table.OwningSchema,
                [
                    new SqlCatalogConstraint("signed", SqlCatalogConstraintKind.Check, [], checkExpression: "-label IS NULL"),
                    new SqlCatalogConstraint("via_cast", SqlCatalogConstraintKind.Check, [], checkExpression: "CAST(qty AS VARCHAR(5)) <> '13'"),
                ]);
            await database.Catalog.PublishTableAsync(stored, [], database.Catalog.GetIndexRegistrations(), replaceExisting: true);
        }

        // Act
        await using var reopenedEngine = CreateEngine("persisted-rules", _rootPath);
        var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("db");
        await using var reopenedSession = await reopened.CreateSessionAsync(CancellationToken.None);

        // Assert: both predicates are enforced as stored.
        await WriteAsync(reopened, reopenedSession, "INSERT INTO t (id, qty) VALUES (1, 1)");
        await WriteFailsAsync(reopened, reopenedSession, "INSERT INTO t (id, qty) VALUES (2, 13)");
        var typeError = await Should.ThrowAsync<DatabaseException>(async () =>
            await reopenedSession.ExecuteAsync("INSERT INTO t (id, label) VALUES (3, 'x')"));
        typeError.Message.ShouldStartWith("COHSQLE003", Case.Sensitive);
        await DdlAsync(reopened, reopenedSession, "ALTER TABLE t DROP COLUMN extra");
        (await RowsAsync(reopenedSession, "SELECT id, label, qty FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, null, 1 });
    }

    /// <summary>
    /// A database on a data-storage format before 4 holds its definitions as they were written,
    /// not as canonical text, and they are not migrated. The format gate refuses the open before
    /// binding could misread a bare DEFAULT — <c>true</c> as the Boolean literal <c>TRUE</c>,
    /// <c>abc</c> as a column reference reported as catalog damage.
    /// </summary>
    /// <param name="storedDefault">The DEFAULT text an older engine stored: the literal's bare value.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: an older-format database with definitions fails the open with a format error")]
    [InlineData("abc")]
    [InlineData("true")]
    [InlineData("+5")]
    public async Task Open_OlderFormatWithDefinitions_ShouldFailWithFormatError(string storedDefault)
    {
        // Arrange
        await using (var engine = CreateEngine("persisted-format", _rootPath))
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("legacy");
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE notes (id INT, body TEXT DEFAULT 'x')");

            var table = Table(database, "notes");
            var columns = table.Columns.ToArray();
            columns[1] = new SqlCatalogColumn("body", columns[1].Type, columns[1].IsNullable, storedDefault);
            var legacy = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, table.PrimaryKeyColumns,
                table.Owner, table.OwningSchema, table.Constraints);
            await database.Catalog.PublishTableAsync(legacy, [], database.Catalog.GetIndexRegistrations(), replaceExisting: true);
            await database.Catalog.SetRecordSpaceFormatVersionAsync(3);
        }

        // Act
        await using var reopenedEngine = CreateEngine("persisted-format", _rootPath);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopenedEngine.OpenDatabaseAsync("legacy"));

        // Assert
        failure.Message.ShouldStartWith("Database 'legacy' uses data-storage format 3", Case.Sensitive);
        failure.Message.ShouldContain($"supports only format {SqlRowCodec.RecordSpaceFormatVersion}", Case.Sensitive);
        failure.Message.ShouldContain("does not upgrade", Case.Sensitive);
        failure.Message.ShouldNotContain("damaged", Case.Insensitive);
    }

    /// <summary>
    /// CREATE TABLE converts every DEFAULT to its column before anything is published, as ADD
    /// COLUMN does, so a default the column cannot store fails the DDL rather than every later
    /// INSERT that omits the column.
    /// </summary>
    /// <param name="ddl">A CREATE TABLE with an unusable DEFAULT.</param>
    /// <param name="column">The column the error names.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: CREATE TABLE rejects a DEFAULT its column cannot store")]
    [InlineData("CREATE TABLE t (id INT, amount INT DEFAULT 'abc')", "amount")]
    [InlineData("CREATE TABLE t (id INT, label VARCHAR(2) DEFAULT 'long')", "label")]
    [InlineData("CREATE TABLE t (id INT, price DECIMAL(5, 2) DEFAULT 1.234)", "price")]
    public async Task CreateTable_DefaultTheColumnCannotStore_ShouldFailTheDdl(string ddl, string column)
    {
        // Arrange
        await using var engine = CreateEngine("persisted-default-check");
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(ddl));

        // Assert
        failure.Message.ShouldStartWith($"Column '{column}': DEFAULT value cannot be stored as", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "t", out _).ShouldBeFalse();
        (await session.ExecuteAsync("CREATE TABLE t (id INT, amount INT DEFAULT '7')")).Status.ShouldBe(QueryResultStatus.Success);
    }

    /// <summary>
    /// A compiled schema's CHECK keeps its author's spelling, while the catalog holds the
    /// canonical text; reapplying the same schema compares the canonical forms and is a no-op.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted definitions: reapplying a compiled schema with a non-canonical CHECK is a no-op")]
    public async Task CompiledSchema_NonCanonicalCheck_ShouldReapplyAsNoOp()
    {
        // Arrange
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", EngineModel.Sql, false, [],
            [
                new CompiledSchemaTable("items", "Tests.Item",
                    [new("id", DatabaseType.Int32, false), new("qty", DatabaseType.Int32, false)],
                    new CompiledSchemaKey("pk_items", ["id"]), [],
                    [new CompiledSchemaConstraint("ck_qty", CompiledSchemaConstraintKind.Check,
                        ["qty"], null, [], new CompiledSchemaExpression("QTY>0   AND qty<=100"))]),
            ], [], [], [], []);

        await using (var engine = CreateEngine("persisted-schema", _rootPath))
        {
            var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("app");

            // Act
            (await database.ApplySchemaAsync(schema)).WasAlreadyApplied.ShouldBeFalse();
            var reapplied = await database.ApplySchemaAsync(schema);

            // Assert
            reapplied.WasAlreadyApplied.ShouldBeTrue();
            Check(database, "items", "ck_qty").CheckExpression.ShouldBe("QTY > 0 AND qty <= 100");
        }

        await using var reopenedEngine = CreateEngine("persisted-schema", _rootPath);
        var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("app");
        (await reopened.ApplySchemaAsync(schema)).WasAlreadyApplied.ShouldBeTrue();
        await using var session = await reopened.CreateSessionAsync(CancellationToken.None);
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await session.ExecuteAsync("INSERT INTO items VALUES (1, 101)"));
    }

    private static SqlCatalogTable Table(SqlDatabaseInstance database, string name)
    {
        database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
        return table;
    }

    private static SqlCatalogConstraint Check(SqlDatabaseInstance database, string table, string name)
        => Table(database, table).Constraints.Single(constraint => constraint.Name == name);

    private static SqlDatabaseEngine CreateEngine(string name)
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = name });

    private static SqlDatabaseEngine CreateEngine(string name, string rootPath)
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = name, RootPath = rootPath });

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
