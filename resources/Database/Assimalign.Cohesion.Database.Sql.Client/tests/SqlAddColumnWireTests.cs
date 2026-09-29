using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Verifies ADD COLUMN defaults and atomic errors through the SQL server and typed client.</summary>
public sealed class SqlAddColumnWireTests
{
    /// <summary>Existing rows and omitted-column inserts receive the same typed default.</summary>
    /// <param name="notNull">Whether the added column rejects explicit null values.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: old and new rows receive literal defaults over the wire")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LiteralDefault_ShouldBackfillOldRowsAndDefaultOmittedInserts(bool notNull)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        await ExecuteAsync(connection, $"ALTER TABLE additions ADD COLUMN extra INT {(notNull ? "NOT NULL " : "")}DEFAULT 7");

        // Assert: the two original rows must already expose the default before any insert or rewrite.
        var oldRows = await QueryAsync(connection, "SELECT * FROM additions ORDER BY id");
        oldRows.Columns.Select(column => column.Name).ShouldBe(["id", "label", "extra"]);
        oldRows.Columns[2].Type.ShouldBe(DatabaseType.Int32);
        oldRows.Select(row => row["id"]).ShouldBe(new object?[] { 1, 2 });
        oldRows.Select(row => row["label"]).ShouldBe(new object?[] { "original", null });
        oldRows.Select(row => row["extra"]).ShouldBe(new object?[] { 7, 7 });

        await ExecuteAsync(connection, "INSERT INTO additions (id, label) VALUES (3, 'omitted')");
        (await QueryAsync(connection, "SELECT extra FROM additions WHERE id = 3")).ShouldHaveSingleItem()[0].ShouldBe(7);

        if (notNull)
        {
            var error = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection,
                "INSERT INTO additions (id, label, extra) VALUES (4, 'explicit null', NULL)"));
            error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            error.ConnectionUsable.ShouldBeTrue();
            (await QueryAsync(connection, "SELECT id FROM additions WHERE id = 4")).ShouldBeEmpty();
        }
        else
        {
            await ExecuteAsync(connection, "INSERT INTO additions (id, label, extra) VALUES (4, 'explicit null', NULL)");
            (await QueryAsync(connection, "SELECT extra FROM additions WHERE id = 4")).ShouldHaveSingleItem()[0].ShouldBeNull();
        }

        // Materializing an update must preserve the synthesized trailing value too.
        await ExecuteAsync(connection, "UPDATE additions SET label = 'updated' WHERE id = 1");
        var updated = await QueryAsync(connection, "SELECT label, extra FROM additions WHERE id = 1");
        updated.ShouldHaveSingleItem()[0].ShouldBe("updated");
        updated[0][1].ShouldBe(7);
    }

    /// <summary>A nonliteral default fails with the CREATE TABLE diagnostic before publishing a column.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: nonliteral defaults reject atomically over the wire")]
    public async Task ExecuteAsync_NonliteralDefault_ShouldRejectAndLeaveTableUnchanged()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert: this statement previously succeeded while silently discarding its expression.
        var error = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection,
            "ALTER TABLE additions ADD COLUMN extra INT DEFAULT (1 + 2)"));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("Column 'extra': only literal DEFAULT values are supported.", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        await AssertUnchangedAsync(connection);

        // The failed attempt must not reserve the name or leave a partly changed row layout.
        await ExecuteAsync(connection, "ALTER TABLE additions ADD COLUMN extra INT DEFAULT 7");
        (await QueryAsync(connection, "SELECT extra FROM additions ORDER BY id"))
            .Select(row => row[0]).ShouldBe(new object?[] { 7, 7 });
    }

    /// <summary>Invalid literal conversions reject atomically rather than publishing unusable metadata.</summary>
    /// <param name="definition">The invalid column type and literal default.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: invalid literal defaults preserve the populated table")]
    [InlineData("INT DEFAULT 2147483648")]
    [InlineData("INT DEFAULT 'not-an-integer'")]
    [InlineData("VARCHAR(2) DEFAULT 'long'")]
    [InlineData("DECIMAL(3, 1) DEFAULT 12.34")]
    [InlineData("DECIMAL(3, 1) DEFAULT 123.4")]
    [InlineData("DECIMAL DEFAULT 0.00000000000000000000000000001")]
    [InlineData("REAL DEFAULT 1e1000")]
    public async Task ExecuteAsync_InvalidLiteralDefault_ShouldRejectAndLeaveTableUnchanged(string definition)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection,
            $"ALTER TABLE additions ADD COLUMN extra {definition}"));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("DEFAULT", Case.Sensitive);
        error.Message.ShouldContain("extra", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        await AssertUnchangedAsync(connection);
    }

    /// <summary>A later row's constraint failure leaves both the column and its backing objects unpublished.</summary>
    /// <param name="constraint">A constraint that the second old row violates.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: failed old-row constraints publish no partial change")]
    [InlineData("DEFAULT 7 UNIQUE")]
    [InlineData("DEFAULT 1 CHECK (extra >= id)")]
    public async Task ExecuteAsync_DefaultViolatesConstraint_ShouldRejectAndLeaveTableUnchanged(string constraint)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection,
            $"ALTER TABLE additions ADD COLUMN extra INT {constraint}"));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.ConnectionUsable.ShouldBeTrue();
        await AssertUnchangedAsync(connection);

        await ExecuteAsync(connection, "ALTER TABLE additions ADD COLUMN extra INT DEFAULT 7");
        (await QueryAsync(connection, "SELECT extra FROM additions ORDER BY id"))
            .Select(row => row[0]).ShouldBe(new object?[] { 7, 7 });
    }

    /// <summary>Required columns cannot be added to live rows without a nonnull default.</summary>
    /// <param name="defaultClause">The absent or null default.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: NOT NULL without a usable default rejects atomically")]
    [InlineData("")]
    [InlineData(" DEFAULT NULL")]
    public async Task ExecuteAsync_NotNullWithoutUsableDefault_ShouldRejectAndLeaveTableUnchanged(string defaultClause)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection,
            $"ALTER TABLE additions ADD COLUMN extra INT NOT NULL{defaultClause}"));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("extra", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        await AssertUnchangedAsync(connection);
    }

    /// <summary>Nullable additions without a value retain null semantics alongside a backfilled sibling.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: nullable missing values remain NULL beside literal defaults")]
    public async Task ExecuteAsync_NullableWithoutDefault_ShouldPreserveNullAlongsideBackfilledValue()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        await ExecuteAsync(connection, "ALTER TABLE additions ADD COLUMN absent INT");
        await ExecuteAsync(connection, "ALTER TABLE additions ADD COLUMN extra INT DEFAULT 7");
        await ExecuteAsync(connection, "INSERT INTO additions (id) VALUES (3)");

        // Assert
        var rows = await QueryAsync(connection, "SELECT id, label, absent, extra FROM additions ORDER BY id");
        rows.Select(row => row["id"]).ShouldBe(new object?[] { 1, 2, 3 });
        rows.Select(row => row["label"]).ShouldBe(new object?[] { "original", null, null });
        rows.Select(row => row["absent"]).ShouldBe(new object?[] { null, null, null });
        rows.Select(row => row["extra"]).ShouldBe(new object?[] { 7, 7, 7 });
    }

    /// <summary>Backfilled strings retain their text and use the declared collation for scans and index probes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: collated defaults compare consistently before and after indexing")]
    public async Task ExecuteAsync_CollatedStringDefault_ShouldPreserveTextAndCollationOverWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        await ExecuteAsync(connection,
            "ALTER TABLE additions ADD COLUMN extra TEXT COLLATE case_accent_insensitive NOT NULL DEFAULT 'CAFÉ'");
        await ExecuteAsync(connection, "INSERT INTO additions (id, label) VALUES (3, 'new')");

        // Assert: matching is folded, but returned text is not normalized.
        var rows = await QueryAsync(connection, "SELECT id, label, extra FROM additions WHERE extra = 'cafe' ORDER BY id");
        rows.Select(row => row["id"]).ShouldBe(new object?[] { 1, 2, 3 });
        rows.Select(row => row["label"]).ShouldBe(new object?[] { "original", null, "new" });
        rows.Select(row => row["extra"]).ShouldBe(new object?[] { "CAFÉ", "CAFÉ", "CAFÉ" });
        (await QueryAsync(connection, "SELECT id FROM additions WHERE extra = 'cafe' COLLATE binary")).ShouldBeEmpty();

        await ExecuteAsync(connection, "CREATE INDEX ix_additions_extra ON additions (extra)");
        (await QueryAsync(connection, "SELECT id FROM additions WHERE extra = 'cafe' ORDER BY id"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
    }

    /// <summary>ADD COLUMN remains forbidden inside an explicit transaction and rollback preserves the prior table.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - ADD COLUMN: explicit transaction restriction preserves schema and rollback")]
    public async Task ExecuteAsync_InsideExplicitTransaction_ShouldRejectWithoutPublishingColumn()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        await ExecuteAsync(connection, "BEGIN");
        await ExecuteAsync(connection, "UPDATE additions SET label = 'uncommitted' WHERE id = 1");

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection,
            "ALTER TABLE additions ADD COLUMN extra INT DEFAULT 7"));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("COHSQLT003", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        await ExecuteAsync(connection, "ROLLBACK");
        await AssertUnchangedAsync(connection);
        await ExecuteAsync(connection, "ALTER TABLE additions ADD COLUMN extra INT DEFAULT 7");
        (await QueryAsync(connection, "SELECT extra FROM additions ORDER BY id"))
            .Select(row => row[0]).ShouldBe(new object?[] { 7, 7 });
    }

    private static async Task SeedAsync(ISqlConnection connection)
    {
        await ExecuteAsync(connection, "CREATE TABLE additions (id INT PRIMARY KEY, label TEXT)");
        await ExecuteAsync(connection, "INSERT INTO additions VALUES (1, 'original'), (2, NULL)");
    }

    private static async Task AssertUnchangedAsync(ISqlConnection connection)
    {
        var rows = await QueryAsync(connection, "SELECT * FROM additions ORDER BY id");
        rows.Columns.Select(column => column.Name).ShouldBe(["id", "label"]);
        rows.Select(row => row["id"]).ShouldBe(new object?[] { 1, 2 });
        rows.Select(row => row["label"]).ShouldBe(new object?[] { "original", null });
    }

    private static Task<long> ExecuteAsync(ISqlConnection connection, string sql)
        => connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();

    private static Task<SqlResultSet> QueryAsync(ISqlConnection connection, string sql)
        => connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();
}
