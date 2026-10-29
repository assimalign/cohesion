using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlIdentityReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - OVERRIDING USER: preserve discarded typed casts and volatile inputs")]
    [InlineData(false, "CAST(100000 AS SMALLINT)")]
    [InlineData(true, "CAST(100000 AS SMALLINT)")]
    [InlineData(false, "CAST(nextval('s') AS SMALLINT)")]
    [InlineData(true, "CAST(nextval('s') AS SMALLINT)")]
    public async Task ExecuteAsync_IgnoredDeferredInput_ShouldAllocateOnlyIdentity(bool merge, string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("override-deferred-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s START 100000");
        await Exec(session, "CREATE TABLE t(id INT GENERATED ALWAYS AS IDENTITY,k INT)");
        string sql = merge
            ? "MERGE INTO t USING (SELECT 1 AS k) s ON t.k=s.k WHEN NOT MATCHED THEN INSERT(id,k) OVERRIDING USER VALUE VALUES(" + expression + ",1)"
            : "INSERT INTO t OVERRIDING USER VALUE VALUES(" + expression + ",1)";
        int allocations = 0;
        db.Catalog.AllocationEntering = () => allocations++;
        try
        {
            // Act
            await Exec(session, sql);

            // Assert
            allocations.ShouldBe(1);
            (await Rows(session, "SELECT id,k FROM t"))[0].ShouldBe(new object?[] { 1, 1 });
            (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(100000L);
            allocations.ShouldBe(2);
        }
        finally { db.Catalog.AllocationEntering = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - OVERRIDING USER corpus: validate CAST input and folded constants before discard")]
    [InlineData(false, "CAST('abc' AS INT)", "CS02022")]
    [InlineData(true, "CAST('abc' AS INT)", "CS02022")]
    [InlineData(false, "ABS(CAST('abc' AS INT))", "CS02022")]
    [InlineData(true, "ABS(CAST('abc' AS INT))", "CS02022")]
    [InlineData(false, "CAST(UPPER('abc') AS INT)", "CS02022")]
    [InlineData(true, "CAST(UPPER('abc') AS INT)", "CS02022")]
    [InlineData(false, "ABS(-9223372036854775808)", "CS02002")]
    [InlineData(true, "ABS(-9223372036854775808)", "CS02002")]
    [InlineData(false, "-(-9223372036854775808)", "CS02002")]
    [InlineData(true, "-(-9223372036854775808)", "CS02002")]
    public async Task ExecuteAsync_IgnoredConstantInput_ShouldRefuseBeforeAllocation(bool merge, string expression, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("override-constant-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id INT GENERATED ALWAYS AS IDENTITY,k INT)");
        string sql = merge
            ? "MERGE INTO t USING (SELECT 1 AS k) s ON t.k=s.k WHEN NOT MATCHED THEN INSERT(id,k) OVERRIDING USER VALUE VALUES(" + expression + ",1)"
            : "INSERT INTO t OVERRIDING USER VALUE VALUES(" + expression + ",1)";
        int allocations = 0;
        db.Catalog.AllocationEntering = () => allocations++;
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => Exec(session, sql));

            // Assert
            failure.Diagnostics[0].Code.ShouldBe(code);
            allocations.ShouldBe(0);
            (await Rows(session, "SELECT COUNT(*) FROM t"))[0][0].ShouldBe(0L);
            await Exec(session, "INSERT INTO t(k) VALUES(1)");
            allocations.ShouldBe(1);
            (await Rows(session, "SELECT id,k FROM t"))[0].ShouldBe(new object?[] { 1, 1 });
        }
        finally { db.Catalog.AllocationEntering = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - OVERRIDING USER: bind and validate ignored assignments")]
    [InlineData(0, "DATE '2026-01-01'")][InlineData(0, "'abc'")][InlineData(0, "missing_function()")]
    [InlineData(1, "DATE '2026-01-01'")][InlineData(1, "'abc'")][InlineData(1, "missing_function()")]
    [InlineData(2, "DATE '2026-01-01'")][InlineData(2, "'abc'")][InlineData(2, "missing_function()")]
    public async Task ExecuteAsync_InvalidIgnoredExpression_ShouldRefuseBeforeAllocation(int shape, string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("override-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id INT GENERATED ALWAYS AS IDENTITY)");
        string sql = shape switch
        {
            0 => "INSERT INTO t OVERRIDING USER VALUE VALUES(" + expression + ")",
            1 => "INSERT INTO t OVERRIDING USER VALUE SELECT " + expression,
            _ => "MERGE INTO t USING (SELECT 1 AS k) s ON t.id=s.k WHEN NOT MATCHED THEN INSERT(id) OVERRIDING USER VALUE VALUES(" + expression + ")",
        };
        int allocations = 0;
        db.Catalog.AllocationEntering = () => allocations++;
        try
        {
            // Act
            await Should.ThrowAsync<DatabaseException>(() => Exec(session, sql));
            // Assert
            allocations.ShouldBe(0);
            (await Rows(session, "SELECT COUNT(*) FROM t"))[0][0].ShouldBe(0L);
        }
        finally { db.Catalog.AllocationEntering = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - OVERRIDING USER: discard typed numeric conversion without execution")]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task ExecuteAsync_IgnoredNumericNarrowing_ShouldAllocateOnlyDefault(int shape)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("override-narrowing-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id SMALLSERIAL)");
        string sql = shape switch
        {
            0 => "INSERT INTO t OVERRIDING USER VALUE VALUES(100000)",
            1 => "INSERT INTO t OVERRIDING USER VALUE SELECT 100000",
            _ => "MERGE INTO t USING (SELECT 1 AS k) s ON t.id=s.k WHEN NOT MATCHED THEN INSERT(id) OVERRIDING USER VALUE VALUES(100000)",
        };
        int allocations = 0;
        db.Catalog.AllocationEntering = () => allocations++;
        try
        {
            // Act
            await Exec(session, sql);
            // Assert
            allocations.ShouldBe(1);
            Convert.ToInt64((await Rows(session, "SELECT id FROM t"))[0][0]).ShouldBe(1L);
        }
        finally { db.Catalog.AllocationEntering = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Identity exhaustion: retain allocator diagnostic")]
    public async Task ExecuteAsync_IdentityExhausted_ShouldKeepValueCode()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("exhaustion-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id SMALLSERIAL)");
        await Exec(session, "ALTER TABLE t ALTER id RESTART WITH 32767");
        await Exec(session, "INSERT INTO t DEFAULT VALUES");
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Exec(session, "INSERT INTO t DEFAULT VALUES"));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS02002");
        (await Rows(session, "SELECT currval('t_id_seq')"))[0][0].ShouldBe(32767L);
    }

}
