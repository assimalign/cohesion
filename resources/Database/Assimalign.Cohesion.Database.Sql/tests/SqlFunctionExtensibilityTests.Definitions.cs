using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Phase E2 part 2: what a stored or declared definition does with the engine's functions. A
/// compiled schema's <c>table.Check(name, sql)</c> binds to the frozen catalog in the build's phase 3,
/// before any file is touched (owner decision 64); a stored CHECK that calls a function the opening
/// engine does not register opens, keeps its reads and fails each write with <c>COHSQLE009</c>, and
/// fails the build of an engine that declares its database (decision 65); and
/// <c>COHESION_SCHEMA.FUNCTIONS</c> lists the catalog and the special forms (decision 66).
/// </summary>
public sealed partial class SqlFunctionExtensibilityTests
{
    private const string UnregisteredFunction = "COHSQLE009";

    /// <summary>
    /// A declared CHECK over a registered immutable function provisions as a schema-owned constraint,
    /// is enforced, and reopens: the next build over the same files binds it again and skips the
    /// applied schema.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a declared table.Check over a registered function provisions, enforces and reopens")]
    public async Task Build_DeclaredCheckOverRegisteredFunction_ShouldProvisionEnforceAndReopen()
    {
        string root = NewRoot();
        try
        {
            // Arrange
            await using (var first = await DeclaringBuilder(root, "is_email(Email) AND LENGTH(Email) < 100").BuildAsync())
            {
                first.TryGetDatabase("shop", out SqlDatabase? created).ShouldBeTrue();
                await using var session = await created!.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "INSERT INTO customers (Id, Email) VALUES (1, 'ann@example.com')");
                await Should.ThrowAsync<SqlConstraintViolationException>(() =>
                    ExecuteAsync(session, "INSERT INTO customers (Id, Email) VALUES (2, 'nobody')"));
            }

            // Act
            await using var second = await DeclaringBuilder(root, "is_email(Email) AND LENGTH(Email) < 100").BuildAsync();
            second.TryGetDatabase("shop", out SqlDatabase? shop).ShouldBeTrue();
            await using var again = await shop!.CreateSessionAsync(CancellationToken.None);
            var violation = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
                ExecuteAsync(again, "INSERT INTO customers (Id, Email) VALUES (3, 'still-nobody')"));
            var volatileRefusal = await Should.ThrowAsync<DatabaseException>(() =>
                ExecuteAsync(again, "CREATE TABLE notes (body TEXT, CONSTRAINT ck CHECK (is_email_volatile(body)))"));
            await ExecuteAsync(again, "CREATE TABLE notes (body TEXT)");
            var volatileAddition = await Should.ThrowAsync<DatabaseException>(() =>
                ExecuteAsync(again, "ALTER TABLE notes ADD CONSTRAINT ck CHECK (is_email_volatile(body))"));

            // Assert
            violation.ShouldNotBeNull();
            second.DeclaredDatabases.ShouldHaveSingleItem().Result.ShouldNotBeNull().WasAlreadyApplied.ShouldBeTrue();
            shop.Catalog.TryGetTable("dbo", "customers", out var customers).ShouldBeTrue();
            var check = customers.Constraints.ShouldHaveSingleItem();
            check.Name.ShouldBe("ck_email");
            check.CheckExpression.ShouldBe("is_email(Email) AND LENGTH(Email) < 100");
            customers.Owner.ShouldBe(DatabaseObjectOwner.Schema);
            volatileRefusal.Message.ShouldStartWith("Function 'is_email_volatile' is VOLATILE, and a CHECK admits only IMMUTABLE functions.", Case.Sensitive);
            volatileAddition.Message.ShouldBe(volatileRefusal.Message);
            (await RowsAsync(again, "SELECT Id FROM customers")).ShouldBe([[1L]]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// The build binds every declared CHECK to the frozen catalog in phase 3 and refuses one that does
    /// not bind with <c>COHSQLP001</c>, naming the engine, the database, the table and the constraint,
    /// before the engine exists, so no file is touched.
    /// </summary>
    /// <param name="predicate">The declared predicate.</param>
    /// <param name="reason">The start of the reason the refusal gives.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: a declared CHECK that does not bind is refused in phase 3, before any file")]
    [InlineData("slugify(Email) <> ''", "Unknown function 'slugify'.")]
    [InlineData("is_email_volatile(Email)",
        "Function 'is_email_volatile' is VOLATILE, and a CHECK admits only IMMUTABLE functions. Register the function as " +
        "SqlFunctionVolatility.Immutable if its result depends on nothing but its arguments.")]
    [InlineData("is_email(Email, 1)",
        "COHSQLE006: Function 'is_email' takes exactly 1 argument but was called with 2. Accepted: is_email(TEXT).")]
    [InlineData("is_email(Id)",
        "COHSQLE006: Function 'is_email' has no overload that accepts argument types (BIGINT). Accepted: is_email(TEXT).")]
    [InlineData("Id > 0) OR (1 = 1", "its SQL text 'Id > 0) OR (1 = 1' is not exactly one SQL expression (")]
    [InlineData("Id > 0 ORDER BY Id", "its SQL text 'Id > 0 ORDER BY Id' is not exactly one SQL expression (")]
    [InlineData("COUNT(Id) > 0", "Function 'COUNT' is not supported in CHECK.")]
    public async Task Build_DeclaredCheckThatDoesNotBind_ShouldBeRefusedBeforeAnyFile(string predicate, string reason)
    {
        string root = NewRoot();
        try
        {
            // Arrange
            SqlDatabaseEngine? product = null;
            var builder = DeclaringBuilder(root, predicate);
            builder.AddWorker(engine =>
            {
                product = engine;
                throw new InvalidOperationException("The engine must not be created.");
            });

            // Act
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync());

            // Assert
            failure.Message.ShouldStartWith(
                "COHSQLP001: SQL engine 'sql-functions' cannot provision database 'shop': CHECK constraint 'ck_email' on table " +
                $"'customers' does not bind: {reason}", Case.Sensitive);
            failure.Message.ShouldEndWith(" Nothing was opened or written.", Case.Sensitive);
            failure.InnerException.ShouldBeAssignableTo<DatabaseException>();
            product.ShouldBeNull();
            Directory.Exists(root).ShouldBeFalse();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// A stored CHECK whose function the opening engine does not resolve binds as unresolved (owner
    /// decision 65): the database opens and its reads proceed; a write that would evaluate the CHECK
    /// fails with <c>COHSQLE009</c> naming the function, the table and the constraint, on a session
    /// that stays usable; a DELETE, an unrelated DROP COLUMN and the constraint's own DROP still run,
    /// after which the table accepts writes again.
    /// </summary>
    /// <param name="registration">How the reopening engine registers <c>is_email</c>.</param>
    /// <param name="reason">The reason the failure gives.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: a stored CHECK over a function the engine no longer registers opens and refuses writes with COHSQLE009")]
    [InlineData("none", "which this engine does not register")]
    [InlineData("aggregate", "which this engine registers as an aggregate")]
    [InlineData("other-signature",
        "which no overload this engine registers accepts (COHSQLE006: Function 'is_email' has no overload that accepts argument types (TEXT). " +
        "Accepted: is_email(BIGINT).)")]
    public async Task Open_StoredCheckOverUnregisteredFunction_ShouldOpenReadAndRefuseWrites(string registration, string reason)
    {
        string root = NewRoot();
        try
        {
            // Arrange
            await SeedCustomersAsync(root);
            await using var reopened = await BuildAsync(functions =>
            {
                switch (registration)
                {
                    case "aggregate":
                        functions.Add(SqlAggregateFunction.Create<long, string, long>("is_email",
                            static () => 0L, static (long count, string _) => count + 1, static (long count) => count));
                        break;
                    case "other-signature":
                        functions.Add(SqlScalarFunction.Create("is_email", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
                        break;
                }
            }, root);

            // Act
            var shop = await reopened.OpenDatabaseAsync("shop");
            await using var session = await shop.CreateSessionAsync(CancellationToken.None);
            var rows = await RowsAsync(session, "SELECT id, email FROM customers ORDER BY id");
            var insert = await Should.ThrowAsync<DatabaseException>(() =>
                ExecuteAsync(session, "INSERT INTO customers (id, email, note) VALUES (2, 'bob@example.com', 'n')"));
            var update = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE customers SET note = 'x'"));
            await ExecuteAsync(session, "ALTER TABLE customers DROP COLUMN note");
            await ExecuteAsync(session, "DELETE FROM customers WHERE id = 1");
            await ExecuteAsync(session, "ALTER TABLE customers DROP CONSTRAINT ck_email");
            await ExecuteAsync(session, "INSERT INTO customers (id, email) VALUES (3, 'nobody')");

            // Assert
            string expected = $"{UnregisteredFunction}: CHECK constraint 'ck_email' on table 'dbo.customers' calls function 'is_email(TEXT)', " +
                $"{reason}, so the constraint cannot be evaluated and the write is refused. Register the function on the engine's builder " +
                "(SqlDatabaseEngineBuilder.Functions), or drop the constraint.";
            rows.ShouldBe([[1L, "ann@example.com"]]);
            insert.Message.ShouldBe(expected);
            update.Message.ShouldBe(expected);
            (await RowsAsync(session, "SELECT id, email FROM customers")).ShouldBe([[3L, "nobody"]]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// An engine that declares the database verifies its stored definitions after provisioning
    /// (step 6, owner decision 65): one that calls a function the engine does not register fails the
    /// build with <c>COHSQLE009</c>, naming the engine, the database, the table, the constraint and
    /// the function, and the engine is disposed; nothing was damaged, so a build that registers the
    /// function again succeeds.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: an engine that declares a database whose stored CHECK it cannot resolve fails its build with COHSQLE009")]
    public async Task Build_DeclaredDatabaseWithUnresolvedStoredCheck_ShouldFailWithCohsqle009()
    {
        string root = NewRoot();
        try
        {
            // Arrange
            await SeedCustomersAsync(root);
            SqlDatabaseEngine? product = null;
            var builder = SqlDatabaseEngine.CreateBuilder("sql-functions");
            builder.Options.RootPath = root;
            builder.AddWorker(engine => new RecordingWorker(product = engine));
            builder.AddDatabase("shop");

            // Act
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync());
            await using var repaired = await BuildAsync(RegisterIsEmail, root, declare: "shop");

            // Assert
            failure.Message.ShouldBe(
                $"{UnregisteredFunction}: SQL engine 'sql-functions', database 'shop': CHECK constraint 'ck_email' on table 'dbo.customers' " +
                "calls function 'is_email(TEXT)', which this engine does not register. The engine declares this database, so its build " +
                "fails before the engine accepts work: register the function on the engine's builder (SqlDatabaseEngineBuilder.Functions), " +
                "or drop the constraint through an engine that does not declare the database.");
            failure.InnerException.ShouldNotBeNull().Message.ShouldStartWith($"{UnregisteredFunction}: CHECK constraint 'ck_email'", Case.Sensitive);
            product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
            repaired.TryGetDatabase("shop", out SqlDatabase? shop).ShouldBeTrue();
            await using var session = await shop!.CreateSessionAsync(CancellationToken.None);
            await Should.ThrowAsync<SqlConstraintViolationException>(() =>
                ExecuteAsync(session, "INSERT INTO customers (id, email) VALUES (2, 'nobody')"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// The imperative apply binds a schema's declared CHECKs before any step runs, as the build does,
    /// and refuses one that does not bind with <c>COHSQLP005</c>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: an imperative apply refuses a declared CHECK that does not bind before any step")]
    public async Task ApplySchemaAsync_CheckThatDoesNotBind_ShouldBeRefusedBeforeAnyStep()
    {
        // Arrange
        await using var engine = await BuildAsync(RegisterIsEmail);
        var shop = await engine.CreateDatabaseAsync("shop");
        var schema = SqlSchema.Compile("shop", declaration => declaration.Table<Customer>("customers", table =>
        {
            table.Key(customer => customer.Id);
            table.Column(customer => customer.Email);
            table.Check("ck_email", "is_email_volatile(Email)");
        }));

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await shop.ApplySchemaAsync(schema));

        // Assert
        failure.Message.ShouldStartWith(
            "COHSQLP005: SQL engine 'sql-functions', database 'shop': SQL schema 'shop': CHECK constraint 'ck_email' on table " +
            "'customers' does not bind: Function 'is_email_volatile' is VOLATILE", Case.Sensitive);
        failure.Message.ShouldEndWith(" Nothing was changed.", Case.Sensitive);
        shop.Catalog.Tables.ShouldBeEmpty();
    }

    /// <summary>
    /// <c>COHESION_SCHEMA.FUNCTIONS</c> lists every function of the engine's catalog, one row per
    /// overload in registration order with its kind, parameter and return types, volatility, NULL
    /// behavior and origin, then the special forms (owner decision 66); it is read-only.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: COHESION_SCHEMA.FUNCTIONS lists the catalog and the special forms")]
    public async Task Query_FunctionsView_ShouldListTheCatalogAndTheSpecialForms()
    {
        // Arrange
        await using var engine = await BuildAsync(functions => functions
            .Add(new ClampFunction())
            .Add(new NullCheckFunction())
            .Add(new MedianFunction()));
        await using var session = await SessionAsync(engine);

        // Act
        var rows = await RowsAsync(session,
            "SELECT FUNCTION_NAME, FUNCTION_KIND, PARAMETER_TYPES, PARAMETER_COUNT, RETURN_TYPE, VOLATILITY, NULL_BEHAVIOR, IS_BUILT_IN " +
            "FROM COHESION_SCHEMA.FUNCTIONS");
        var registered = await RowsAsync(session, "SELECT FUNCTION_NAME FROM COHESION_SCHEMA.FUNCTIONS WHERE IS_BUILT_IN = 'NO' ORDER BY FUNCTION_NAME");
        var readOnly = await Should.ThrowAsync<DatabaseException>(() =>
            ExecuteAsync(session, "INSERT INTO COHESION_SCHEMA.FUNCTIONS (FUNCTION_NAME) VALUES ('x')"));

        // Assert
        rows.Select(row => (string)row[0]!).ShouldBe([
            "UPPER", "LOWER", "LENGTH", "ABS", "COUNT", "COUNT", "SUM", "AVG", "MIN", "MAX",
            "clamp", "is_missing", "median",
            "COALESCE", "NULLIF", "CASE", "CAST", "EXTRACT",
        ]);
        rows[4].ShouldBe(["COUNT", "AGGREGATE", "*", 0L, "BIGINT", "VOLATILE", "RETURNS NULL ON NULL INPUT", "YES"]);
        rows[5].ShouldBe(["COUNT", "AGGREGATE", "ANY", 1L, "BIGINT", "VOLATILE", "RETURNS NULL ON NULL INPUT", "YES"]);
        rows[10].ShouldBe(["clamp", "SCALAR", "BIGINT, BIGINT, BIGINT", 3L, "BIGINT", "IMMUTABLE", "RETURNS NULL ON NULL INPUT", "NO"]);
        rows[11].ShouldBe(["is_missing", "SCALAR", "ANY", 1L, "BOOLEAN", "IMMUTABLE", "CALLED ON NULL INPUT", "NO"]);
        rows[12].ShouldBe(["median", "AGGREGATE", "DOUBLE", 1L, "DOUBLE", "VOLATILE", "RETURNS NULL ON NULL INPUT", "NO"]);
        rows[13].ShouldBe(["COALESCE", "SPECIAL FORM", null, null, null, null, null, "YES"]);
        registered.ShouldBe([["clamp"], ["is_missing"], ["median"]]);
        readOnly.Message.ShouldBe("System view 'COHESION_SCHEMA.FUNCTIONS' is read-only.");
    }

    /// <summary>
    /// A sign over a constant binds as the constant it spells, as PostgreSQL's grammar reads <c>-5</c>,
    /// so an immutable call over it folds once (the design's Q6, <c>ABS(-5)</c>); a sign that fails
    /// over its constant is kept and fails only when a row reaches it, as before.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: an immutable call over a signed constant folds once, and a failing sign still fails per row")]
    public async Task ExecuteAsync_CallOverSignedConstant_ShouldFoldOnce()
    {
        // Arrange
        var doubled = new CountingFunction("doubled", SqlFunctionVolatility.Immutable);
        await using var engine = await BuildAsync(functions => functions.Add(doubled));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2), (3)");
        await ExecuteAsync(session, "CREATE TABLE empty_rows (id INT)");

        // Act
        var folded = await RowsAsync(session, "SELECT doubled(-21), ABS(-5), -(-7) FROM t");
        var overEmpty = await RowsAsync(session, "SELECT -(-9223372036854775808) FROM empty_rows");
        var overflow = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT -(-9223372036854775808) FROM t"));

        // Assert
        folded.ShouldBe([[-42L, 5L, 7L], [-42L, 5L, 7L], [-42L, 5L, 7L]]);
        doubled.Calls.ShouldBe(1);
        overEmpty.ShouldBeEmpty();
        overflow.Message.ShouldStartWith("COHSQLE002", Case.Sensitive);
    }

    // A database 'shop' whose ad-hoc CHECK calls is_email, holding one row, written by an engine that
    // registers is_email and then closed.
    private static async Task SeedCustomersAsync(string root)
    {
        await using var engine = await BuildAsync(RegisterIsEmail, root);
        var shop = await engine.CreateDatabaseAsync("shop");
        await using var session = await shop.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE customers (id BIGINT, email TEXT, note TEXT, CONSTRAINT ck_email CHECK (is_email(email)))");
        await ExecuteAsync(session, "INSERT INTO customers (id, email) VALUES (1, 'ann@example.com')");
    }

    private static SqlDatabaseEngineBuilder DeclaringBuilder(string root, string predicate)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("sql-functions");
        builder.Options.RootPath = root;
        RegisterIsEmail(builder.Functions);
        builder.AddDatabase("shop", database => database.Schema(schema => schema.Table<Customer>("customers", table =>
        {
            table.Key(customer => customer.Id);
            table.Column(customer => customer.Email);
            table.Check("ck_email", predicate);
        })));
        return builder;
    }

    private static void RegisterIsEmail(SqlFunctionCollection functions)
        => functions
            .Add(SqlScalarFunction.Create("is_email", static (string value) => value.Contains('@'), SqlFunctionVolatility.Immutable))
            .Add(SqlScalarFunction.Create("is_email_volatile", static (string value) => value.Contains('@')));

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "cohesion-function-definitions", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed record Customer(long Id, string Email);
}
