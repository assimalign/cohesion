using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Domains and the cast registry (phase E3 of the engine extensibility program, owner decision 68):
/// a domain is registered on the engine through the same type catalog the built-in types resolve
/// through, a column of a domain stores its base type and keeps the domain's name in the catalog,
/// every write that assigns the column evaluates the domain's check, an opening engine binds the
/// name again (and one that no longer registers the domain opens the database and refuses writes to
/// the column with <c>COHSQLE010</c>), and <c>CAST</c> runs the application's casts exactly as the
/// built-in ones.
/// </summary>
public sealed class SqlDomainTests
{
    private const string UnregisteredType = "COHSQLE010";

    /// <summary>The design's §3.7 domain: <c>email</c> over <c>VARCHAR(320)</c>, checked by <c>is_email</c>.</summary>
    private static SqlType Email() => SqlType.CreateDomain("email", SqlType.VarChar(320),
        SqlScalarFunction.Create("is_email", static (string value) => value.Contains('@'), SqlFunctionVolatility.Immutable));

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: DDL names a domain, its column stores the base type, and every write that assigns it is checked")]
    public async Task DomainColumn_ShouldStoreTheBaseTypeAndCheckEveryAssignment()
    {
        // Arrange
        await using var engine = await BuildAsync(types => types.Add(Email()));
        var database = await engine.CreateDatabaseAsync("crm");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await ExecuteAsync(session, "CREATE TABLE contacts (id BIGINT PRIMARY KEY, email email NOT NULL, note TEXT, backup EMAIL)");
        await ExecuteAsync(session, "INSERT INTO contacts (id, email, note) VALUES (1, 'ann@example.com', 'first')");
        var insertViolation = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            ExecuteAsync(session, "INSERT INTO contacts (id, email) VALUES (2, 'nobody')"));
        await ExecuteAsync(session, "UPDATE contacts SET note = 'changed' WHERE id = 1");
        var updateViolation = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            ExecuteAsync(session, "UPDATE contacts SET backup = 'nobody' WHERE id = 1"));
        await ExecuteAsync(session, "UPDATE contacts SET backup = 'ann@backup.example' WHERE id = 1");
        await ExecuteAsync(session, "INSERT INTO contacts (id, email, backup) VALUES (3, 'bob@example.com', NULL)");
        var selectViolation = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            ExecuteAsync(session, "INSERT INTO contacts (id, email) SELECT id + 10, note FROM contacts WHERE id = 1"));

        // Assert: the base type is stored and returned; the catalog keeps the domain's name.
        insertViolation.ConstraintKind.ShouldBe("DOMAIN");
        insertViolation.ConstraintName.ShouldBe("email");
        insertViolation.Table.ShouldBe("dbo.contacts");
        insertViolation.Message.ShouldBe("DOMAIN constraint 'email' on 'dbo.contacts' was violated.");
        updateViolation.ConstraintName.ShouldBe("email");
        selectViolation.ConstraintName.ShouldBe("email");
        await using var result = (await ExecuteAsync(session, "SELECT email, backup FROM contacts ORDER BY id")).ShouldBeAssignableTo<QueryResultSet>();
        result.Columns[0].Type.ShouldBe(DatabaseType.String);
        (await ReadAsync(result)).ShouldBe([["ann@example.com", "ann@backup.example"], ["bob@example.com", null]]);
        database.Catalog.TryGetTable("dbo", "contacts", out var table).ShouldBeTrue();
        table.Columns.Select(column => (column.DomainName, column.Type.Type, column.Type.MaxLength)).ShouldBe([
            (null, DatabaseType.Int64, null),
            ("email", DatabaseType.String, 320),
            (null, DatabaseType.String, null),
            ("email", DatabaseType.String, 320),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: ALTER TABLE ADD COLUMN of a domain checks the default each existing row is assigned")]
    public async Task AddColumn_OfADomain_ShouldCheckTheBackfilledDefault()
    {
        // Arrange
        await using var engine = await BuildAsync(types => types.Add(Email()));
        var database = await engine.CreateDatabaseAsync("crm");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE contacts (id BIGINT PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE empty_contacts (id BIGINT PRIMARY KEY)");
        await ExecuteAsync(session, "INSERT INTO contacts VALUES (1)");

        // Act
        var refused = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            ExecuteAsync(session, "ALTER TABLE contacts ADD COLUMN email email DEFAULT 'nobody'"));
        await ExecuteAsync(session, "ALTER TABLE contacts ADD COLUMN email email DEFAULT 'ops@example.com'");
        await ExecuteAsync(session, "ALTER TABLE empty_contacts ADD COLUMN email email DEFAULT 'nobody'");
        var defaultViolation = await Should.ThrowAsync<SqlConstraintViolationException>(() =>
            ExecuteAsync(session, "INSERT INTO empty_contacts (id) VALUES (1)"));

        // Assert: the refused alteration changed nothing; an INSERT that takes the default is checked.
        refused.ConstraintName.ShouldBe("email");
        defaultViolation.ConstraintName.ShouldBe("email");
        (await RowsAsync(session, "SELECT id, email FROM contacts")).ShouldBe([[1L, "ops@example.com"]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: DDL refuses an unknown type with COHSQLE010 and a domain written with arguments")]
    public async Task Ddl_UnknownTypeOrDomainWithArguments_ShouldBeRefused()
    {
        // Arrange
        await using var engine = await BuildAsync(types => types.Add(Email()));
        var database = await engine.CreateDatabaseAsync("crm");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var unknown = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE TABLE t (id INT, code nosuch)"));
        var withArguments = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE TABLE t (id INT, address email(40))"));
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        var added = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE t ADD COLUMN code nosuch"));

        // Assert
        unknown.Message.ShouldBe($"{UnregisteredType}: Column 'code': type 'nosuch' does not exist: it is neither a built-in type nor a domain " +
            "this engine registers. Register the domain on the engine's builder (SqlDatabaseEngineBuilder.Types) before Build.");
        withArguments.Message.ShouldContain("domain 'email' takes no type arguments", Case.Sensitive);
        added.Message.ShouldStartWith($"{UnregisteredType}: Column 'code': type 'nosuch' does not exist", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: CAST to a domain converts to its base type, applies its length and its check")]
    public async Task Cast_ToADomain_ShouldApplyTheBaseTypeAndTheCheck()
    {
        // Arrange
        await using var engine = await BuildAsync(types => types.Add(Email()).Add(SqlType.CreateDomain("code", SqlType.VarChar(3))));
        var database = await engine.CreateDatabaseAsync("crm");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (12)");

        // Act
        var converted = await RowsAsync(session, "SELECT CAST('ann@example.com' AS email), CAST(NULL AS email), CAST(id AS code) FROM t");
        var refused = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST('nobody' AS email) FROM t"));
        var tooLong = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST(id * 1000 AS code) FROM t"));
        await using var typed = (await ExecuteAsync(session, "SELECT CAST(id AS code) FROM t")).ShouldBeAssignableTo<QueryResultSet>();

        // Assert
        converted.ShouldBe([["ann@example.com", null, "12"]]);
        refused.Message.ShouldBe("CAST to email failed: the value violates the check of domain 'email' (is_email(TEXT)).");
        tooLong.Message.ShouldContain("String length exceeds 3", Case.Sensitive);
        typed.Columns[0].Type.ShouldBe(DatabaseType.String);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: CAST runs an application's casts to and from its domain, preferred to its base type's")]
    public async Task Cast_ApplicationCasts_ShouldRunThroughTheRegistry()
    {
        // Arrange: cents, a domain over BIGINT, written and read as "$1.23".
        var cents = SqlType.CreateDomain("cents", SqlType.BigInt);
        await using var engine = await BuildAsync(types => types
            .Add(cents)
            .AddCast(SqlCast.Create<string, long>(SqlType.Text, cents,
                static text => (long)(decimal.Parse(text.TrimStart('$'), CultureInfo.InvariantCulture) * 100), SqlCastContext.Explicit))
            .AddCast(SqlCast.Create<long, string>(cents, SqlType.Text,
                static value => "$" + (value / 100m).ToString("0.00", CultureInfo.InvariantCulture), SqlCastContext.Assignment)));
        var database = await engine.CreateDatabaseAsync("shop");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE prices (id INT, amount cents)");
        await ExecuteAsync(session, "INSERT INTO prices VALUES (1, 123)");

        // Act
        var rows = await RowsAsync(session,
            "SELECT CAST(amount AS TEXT), CAST(id AS TEXT), CAST('$4.56' AS cents), CAST(CAST('$4.56' AS cents) AS TEXT), " +
            "CAST(amount AS BIGINT), CAST('7' AS BIGINT) FROM prices");

        // Assert: the domain's casts where the plan knows the domain, the built-in ones otherwise.
        rows.ShouldBe([["$1.23", "1", 456L, "$4.56", 123L, 7L]]);
        engine.Types.FindCast(cents, SqlType.Text).ShouldNotBeNull().Context.ShouldBe(SqlCastContext.Assignment);
        engine.Types.FindCast(SqlType.Text, SqlType.Date).ShouldNotBeNull().Context.ShouldBe(SqlCastContext.Explicit);
        engine.Types.FindCast(SqlType.Integer, SqlType.BigInt).ShouldNotBeNull().Context.ShouldBe(SqlCastContext.Implicit);
        engine.Types.Find("cents").ShouldBeSameAs(cents);
        engine.Types.Find("int").ShouldBeSameAs(SqlType.Integer);
        engine.Types.Find("nosuch").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a string literal reaches a typed DATE parameter through CAST")]
    public async Task Cast_StringLiteralToDate_ShouldReachATypedFunctionParameter()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-domains");
        builder.Functions.Add(SqlScalarFunction.Create("day_of_year", static (DateOnly date) => date.DayOfYear, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("dates");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");

        // Act
        var rows = await RowsAsync(session, "SELECT day_of_year(CAST('2026-02-01' AS DATE)) FROM t");
        var withoutCast = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT day_of_year('2026-02-01') FROM t"));

        // Assert: literal typing is unchanged, so the uncast literal is still TEXT, which no overload takes.
        rows.ShouldBe([[32]]);
        withoutCast.Message.ShouldStartWith("COHSQLE006:", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a reopened database binds its columns' domains again, and without them opens and refuses writes to them with COHSQLE010")]
    public async Task Reopen_DomainColumns_ShouldBindAgainOrRefuseWritesWhenUnregistered()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-domain", Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange
            await using (var engine = await BuildAsync(types => types.Add(Email()), root))
            {
                var database = await engine.CreateDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "CREATE TABLE contacts (id BIGINT PRIMARY KEY, email email, note TEXT)");
                await ExecuteAsync(session, "INSERT INTO contacts VALUES (1, 'ann@example.com', 'a')");
            }

            // Act 1: an engine that registers the domain binds it again and enforces its check.
            await using (var engine = await BuildAsync(types => types.Add(Email()), root))
            {
                var database = await engine.OpenDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO contacts VALUES (2, 'nobody', 'b')"));
                await ExecuteAsync(session, "INSERT INTO contacts VALUES (2, 'bob@example.com', 'b')");
            }

            // Act 2: an engine that does not register it opens the database; reads and writes that do
            // not assign the column proceed, and writes that do fail with COHSQLE010.
            await using (var engine = await BuildAsync(static _ => { }, root))
            {
                var database = await engine.OpenDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                var rows = await RowsAsync(session, "SELECT id, email FROM contacts ORDER BY id");
                var insert = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO contacts (id, note) VALUES (3, 'c')"));
                var update = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE contacts SET email = NULL WHERE id = 1"));
                await ExecuteAsync(session, "UPDATE contacts SET note = 'changed' WHERE id = 1");
                await ExecuteAsync(session, "DELETE FROM contacts WHERE id = 2");

                rows.ShouldBe([[1L, "ann@example.com"], [2L, "bob@example.com"]]);
                insert.Message.ShouldBe($"{UnregisteredType}: Column 'email' of table 'dbo.contacts' is of domain 'email', which this engine does not " +
                    "register, so the column cannot be written and the write is refused. Register the domain on the engine's builder " +
                    "(SqlDatabaseEngineBuilder.Types), or drop the column.");
                update.Message.ShouldStartWith($"{UnregisteredType}: Column 'email' of table 'dbo.contacts' is of domain 'email'", Case.Sensitive);
                (await RowsAsync(session, "SELECT id, note FROM contacts")).ShouldBe([[1L, "changed"]]);
            }

            // Act 3: a domain of the name over another storage type does not bind either.
            await using (var engine = await BuildAsync(static types => types.Add(SqlType.CreateDomain("email", SqlType.BigInt)), root))
            {
                var database = await engine.OpenDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO contacts (id, email) VALUES (4, 'x')")))
                    .Message.ShouldContain("which this engine registers over BIGINT, but the column stores TEXT", Case.Sensitive);
            }

            // Act 4: an engine that declares the database fails its build instead.
            var declaring = SqlDatabaseEngine.CreateBuilder("sql-domains");
            declaring.Options.RootPath = root;
            declaring.AddDatabase("crm");
            var refused = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await declaring.BuildAsync());
            refused.Message.ShouldStartWith($"{UnregisteredType}: SQL engine 'sql-domains', database 'crm': Column 'email' of table 'dbo.contacts' " +
                "is of domain 'email', which this engine does not register.", Case.Sensitive);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: CreateDomain refuses a name, base type or check the rules exclude")]
    public void CreateDomain_InvalidArguments_ShouldBeRefused()
    {
        var boolean = SqlScalarFunction.Create("ok", static (string value) => value.Length > 0, SqlFunctionVolatility.Immutable);
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("1email", SqlType.Text)).Message.ShouldContain("not an identifier");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("select", SqlType.Text)).Message.ShouldContain("SQL keyword");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("varchar", SqlType.Text)).Message.ShouldContain("built-in SQL type name");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("int4", SqlType.Text)).Message.ShouldContain("built-in SQL type name");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("anyelement", SqlType.Text)).Message.ShouldContain("pseudo-type");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("d", SqlType.Any)).ParamName.ShouldBe("baseType");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("d", SqlType.CreateDomain("base_text", SqlType.Text))).ParamName.ShouldBe("baseType");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("d", SqlType.BigInt, boolean)).Message.ShouldContain("one argument of the domain's base type BIGINT");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("d", SqlType.Text,
            SqlScalarFunction.Create("len", static (string value) => (long)value.Length, SqlFunctionVolatility.Immutable))).Message.ShouldContain("must return BOOLEAN");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("d", SqlType.Text,
            SqlScalarFunction.Create("volatile_ok", static (string value) => value.Length > 0))).Message.ShouldContain("is VOLATILE");

        // A check of an implicitly widened parameter type, or a pseudo-type, is accepted.
        var widened = SqlType.CreateDomain("positive", SqlType.Integer,
            SqlScalarFunction.Create("is_positive", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
        widened.IsDomain.ShouldBeTrue();
        widened.BaseType.ShouldBeSameAs(SqlType.Integer);
        widened.Storage.ShouldBeSameAs(SqlType.Integer.Storage);
        widened.Check!.Name.ShouldBe("is_positive");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: the type collection refuses a built-in type, a duplicate domain, a built-in cast and a cast of an unregistered domain")]
    public async Task Types_Registration_ShouldRefuseWhatTheRulesExclude()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-domains");
        var types = builder.Types;
        var email = Email();
        int builtIns = types.Count;
        int builtInCasts = types.Casts.Count;

        // Act / Assert
        types.Add(email).Count.ShouldBe(builtIns + 1);
        types.Contains("EMAIL").ShouldBeTrue();
        Should.Throw<ArgumentException>(() => types.Add(SqlType.Text)).Message.ShouldContain("is not a domain");
        Should.Throw<InvalidOperationException>(() => types.Add(SqlType.CreateDomain("Email", SqlType.Text))).Message.ShouldContain("registered already");
        Should.Throw<InvalidOperationException>(() => types.AddCast(SqlCast.Create<long, string>(SqlType.BigInt, SqlType.Text, static value => "x", SqlCastContext.Explicit)))
            .Message.ShouldContain("between two built-in types");
        Should.Throw<InvalidOperationException>(() => types.AddCast(SqlCast.Create<string, string>(SqlType.Text, SqlType.CreateDomain("other", SqlType.Text),
            static value => value, SqlCastContext.Explicit))).Message.ShouldContain("which this collection does not register");
        types.AddCast(SqlCast.Create<string, string>(SqlType.Text, email, static value => value.Trim(), SqlCastContext.Explicit));
        Should.Throw<InvalidOperationException>(() => types.AddCast(SqlCast.Create<string, string>(SqlType.VarChar(4), email, static value => value, SqlCastContext.Implicit)))
            .Message.ShouldContain("is already registered");
        types.Casts.Count.ShouldBe(builtInCasts + 1);
        Should.Throw<ArgumentException>(() => SqlCast.Create<long, string>(SqlType.Text, email, static value => "x", SqlCastContext.Explicit))
            .Message.ShouldContain("TSource maps to BIGINT");
        Should.Throw<ArgumentException>(() => SqlCast.Create<string, string>(SqlType.Text, SqlType.VarChar(3), static value => value, SqlCastContext.Explicit))
            .Message.ShouldContain("converts a type to itself");
        Should.Throw<ArgumentOutOfRangeException>(() => SqlCast.Create<string, string>(SqlType.Text, email, static value => value, (SqlCastContext)9));

        await (await builder.BuildAsync()).DisposeAsync();
        Should.Throw<InvalidOperationException>(() => types.Add(SqlType.CreateDomain("late", SqlType.Text)));
        Should.Throw<InvalidOperationException>(() => types.AddCast(SqlCast.Create<string, string>(email, SqlType.Text, static value => value, SqlCastContext.Explicit)));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a typed schema declares domains and columns of named types, provisioned and applied once")]
    public async Task DeclaredSchema_WithDomains_ShouldProvisionTheColumnsAndSkipOnReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-domain-schema", Guid.NewGuid().ToString("N"));
        try
        {
            // Act 1
            await using (var engine = await DeclareAsync(root))
            {
                var database = await engine.OpenDatabaseAsync("shop");
                database.Catalog.TryGetTable("dbo", "customers", out var table).ShouldBeTrue();
                table.Columns.Select(column => (column.Name, column.DomainName, column.Type.Type, column.Type.MaxLength, column.Type.Precision))
                    .ShouldBe([
                        ("Id", null, DatabaseType.Int64, null, null),
                        ("Email", "email", DatabaseType.String, 320, null),
                        ("Code", null, DatabaseType.String, 16, null),
                        ("Balance", "Money", DatabaseType.Decimal, null, 18),
                    ]);
                engine.Types.Find("money").ShouldNotBeNull().BaseType!.Storage.Scale.ShouldBe(2);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await Should.ThrowAsync<SqlConstraintViolationException>(() =>
                    ExecuteAsync(session, "INSERT INTO customers VALUES (1, 'nobody', 'A', 1.5)"));
                await ExecuteAsync(session, "INSERT INTO customers VALUES (1, 'ann@example.com', 'A', 1.5)");
            }

            // Act 2: the same declaration again is already applied.
            await using (var engine = await DeclareAsync(root))
            {
                var database = await engine.OpenDatabaseAsync("shop");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                (await RowsAsync(session, "SELECT Email, Balance FROM customers")).ShouldBe([["ann@example.com", 1.5m]]);
                engine.FindDeclaration(database.Name)!.Result.ShouldNotBeNull().WasAlreadyApplied.ShouldBeTrue();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static Task<SqlDatabaseEngine> DeclareAsync(string root)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("sql-domain-schema");
            builder.Options.RootPath = root;
            builder.Types.Add(Email());
            builder.AddDatabase("shop", database => database.Schema(schema =>
            {
                schema.Type<Money>(type => type.Decimal(18, 2));
                schema.Table<Customer>("customers", table =>
                {
                    table.Key(customer => customer.Id);
                    table.Column(customer => customer.Email, "email");
                    table.Column(customer => customer.Code, "VARCHAR(16)");
                    table.Column(customer => customer.Balance);
                });
            }));
            return builder.BuildAsync().AsTask();
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Domains: the build refuses a declared column type it cannot provision before touching any file")]
    [InlineData("nosuch", "COHSQLE010: Column 'Email': type 'nosuch' does not exist")]
    [InlineData("BIGINT", "Column 'Email' is declared with type 'BIGINT', which is stored as BIGINT, but its member is stored as TEXT")]
    [InlineData("email(3)", "domain 'email' takes no type arguments")]
    public async Task DeclaredSchema_WithAColumnTypeTheEngineCannotProvision_ShouldBeRefused(string sqlType, string reason)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-domain-refused", Guid.NewGuid().ToString("N"));
        var builder = SqlDatabaseEngine.CreateBuilder("sql-domain-refused");
        builder.Options.RootPath = root;
        builder.Types.Add(Email());
        builder.AddDatabase("shop", database => database.Schema(schema => schema.Table<Customer>("customers", table =>
        {
            table.Key(customer => customer.Id);
            table.Column(customer => customer.Email, sqlType);
        })));

        // Act
        var refused = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync());

        // Assert
        refused.Message.ShouldStartWith("COHSQLP001: SQL engine 'sql-domain-refused' cannot provision database 'shop': ", Case.Sensitive);
        refused.Message.ShouldContain(reason, Case.Sensitive);
        refused.Message.ShouldEndWith("Nothing was opened or written.", Case.Sensitive);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a schema domain that conflicts with a registered type is refused, and an imperative apply needs the engine's domains")]
    public async Task SchemaDomains_ConflictOrMissing_ShouldBeRefused()
    {
        // Arrange
        var conflicting = SqlDatabaseEngine.CreateBuilder("sql-domain-conflict");
        conflicting.Types.Add(SqlType.CreateDomain("Money", SqlType.Decimal(10, 2)));
        conflicting.AddDatabase("shop", database => database.Schema(schema =>
        {
            schema.Type<Money>(type => type.Decimal(18, 2));
            schema.Table<Customer>("customers", table => table.Key(customer => customer.Id));
        }));
        SqlCompiledSchema compiled = SqlSchema.Compile("shop", schema =>
        {
            schema.Type<Money>(type => type.Decimal(18, 2));
            schema.Table<Customer>("customers", table =>
            {
                table.Key(customer => customer.Id);
                table.Column(customer => customer.Balance);
            });
        });

        // Act
        var conflict = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await conflicting.BuildAsync());
        await using var plain = SqlDatabaseEngine.Create("sql-domain-plain", new SqlDatabaseEngineOptions());
        var database = await plain.CreateDatabaseAsync("shop");
        var missing = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(compiled));

        // Assert
        conflict.Message.ShouldContain("its schema conflicts with the engine's types: it declares domain 'Money' over NUMERIC(18, 2), " +
            "but the engine registers domain 'Money' over NUMERIC(10, 2).", Case.Sensitive);
        missing.Message.ShouldStartWith("COHSQLP005: SQL engine 'sql-domain-plain', database 'shop': SQL schema 'shop': The schema declares domain 'Money' " +
            "over NUMERIC(18, 2), which this engine does not register with that base type", Case.Sensitive);
        database.Catalog.Tables.ShouldBeEmpty();
    }

    private static async Task<SqlDatabaseEngine> BuildAsync(Action<SqlTypeCollection> register, string? rootPath = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("sql-domains");
        if (rootPath is not null)
        {
            builder.Options.RootPath = rootPath;
        }

        register(builder.Types);
        return await builder.BuildAsync();
    }

    private static Task<QueryResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<QueryResultSet>();
        return await ReadAsync(result);
    }

    private static async Task<List<object?[]>> ReadAsync(QueryResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    private readonly record struct Money(decimal Amount);
    private sealed record Customer(long Id, string Email, string Code, Money Balance);
}
