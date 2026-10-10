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
        var refused = await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(session, "SELECT CAST('nobody' AS email) FROM t"));
        var tooLong = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST(id * 1000 AS code) FROM t"));
        await using var typed = (await ExecuteAsync(session, "SELECT CAST(id AS code) FROM t")).ShouldBeAssignableTo<QueryResultSet>();

        // Assert: the check's refusal is the one error kind a write to the domain raises.
        converted.ShouldBe([["ann@example.com", null, "12"]]);
        refused.Message.ShouldBe("CAST to email failed: the value violates the check of domain 'email' (is_email(TEXT)).");
        (refused.ConstraintKind, refused.ConstraintName, refused.Table).ShouldBe(("DOMAIN", "email", string.Empty));
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
                    .Message.ShouldContain("which this engine registers over BIGINT, but the column stores VARCHAR(320)", Case.Sensitive);
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
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("\u00e9mail", SqlType.Text)).Message.ShouldContain("must start with an ASCII letter");
        Should.Throw<ArgumentException>(() => SqlType.CreateDomain("caf\u00e9", SqlType.Text)).Message.ShouldContain("only ASCII letters, digits and underscores");
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: the type collection refuses a built-in type, a duplicate domain, a registered pair and a cast of an unregistered domain")]
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
            .Message.ShouldBe("A cast from BIGINT to TEXT is already registered by the standard library; a cast cannot be replaced.");
        Should.Throw<InvalidOperationException>(() => types.AddCast(SqlCast.Create<string, string>(SqlType.Text, SqlType.CreateDomain("other", SqlType.Text),
            static value => value, SqlCastContext.Explicit))).Message.ShouldContain("which this collection does not register");
        types.AddCast(SqlCast.Create<string, string>(SqlType.Text, email, static value => value.Trim(), SqlCastContext.Explicit));
        Should.Throw<InvalidOperationException>(() => types.AddCast(SqlCast.Create<string, string>(SqlType.VarChar(4), email, static value => value, SqlCastContext.Implicit)))
            .Message.ShouldBe("A cast from VARCHAR(4) to email is already registered; a cast cannot be replaced.");
        types.Casts.Count.ShouldBe(builtInCasts + 1);

        // Contains answers as the frozen catalog's Find does: built-in aliases and domains.
        foreach (string alias in (string[])["INT", "int8", "VARCHAR", "BOOL", "TimestampTz", "GUID"])
        {
            types.Contains(alias).ShouldBeTrue(alias);
        }
        types.Contains("nosuch").ShouldBeFalse();
        types.Contains("ANYELEMENT").ShouldBeFalse();
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: an application registers a cast between built-in types the standard library leaves out, through the same AddCast")]
    public async Task AddCast_BetweenBuiltInTypes_ShouldFillTheStandardLibrarysGaps()
    {
        // Arrange: DOUBLE to TEXT and BOOLEAN to INTEGER, which the standard library does not convert.
        await using var engine = await BuildAsync(static types => types
            .AddCast(SqlCast.Create<double, string>(SqlType.Double, SqlType.Text,
                static value => value.ToString("R", CultureInfo.InvariantCulture), SqlCastContext.Assignment))
            .AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer, static value => value ? 1 : 0, SqlCastContext.Explicit)));
        var database = await engine.CreateDatabaseAsync("metrics");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE m (id INT, ratio DOUBLE)");
        await ExecuteAsync(session, "INSERT INTO m VALUES (1, 0.25)");
        await using var plain = SqlDatabaseEngine.Create("sql-domains-plain", new SqlDatabaseEngineOptions());
        var plainDatabase = await plain.CreateDatabaseAsync("metrics");
        await using var plainSession = await plainDatabase.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(plainSession, "CREATE TABLE m (id INT, ratio DOUBLE)");

        // Act
        var rows = await RowsAsync(session, "SELECT CAST(ratio AS TEXT), CAST(TRUE AS INT), CAST(id = 2 AS INTEGER) FROM m");
        var without = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(plainSession, "SELECT CAST(ratio AS TEXT) FROM m"));

        // Assert: the engine without the application's cast refuses the pair while planning.
        rows.ShouldBe([["0.25", 1, 0]]);
        engine.Types.FindCast(SqlType.Double, SqlType.Text).ShouldNotBeNull().Context.ShouldBe(SqlCastContext.Assignment);
        plain.Types.FindCast(SqlType.Double, SqlType.Text).ShouldBeNull();
        without.Message.ShouldStartWith("COHSQLE011: Cannot CAST type DOUBLE to TEXT: ", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a value of a domain cast to that domain is relabeled, not converted, and still checked")]
    public async Task Cast_ToItsOwnDomain_ShouldRelabelAndStillCheck()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-domain-relabel", Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange: the domain's own casts to and from TEXT, which a cast to itself must not run.
            var email = Email();
            await using (var engine = await BuildAsync(types => types.Add(email)
                .AddCast(SqlCast.Create<string, string>(email, SqlType.Text, static _ => "masked", SqlCastContext.Assignment))
                .AddCast(SqlCast.Create<string, string>(SqlType.Text, email, static text => text.Trim().ToLowerInvariant(), SqlCastContext.Explicit)), root))
            {
                var database = await engine.CreateDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "CREATE TABLE c (id INT, e email)");
                await ExecuteAsync(session, "INSERT INTO c VALUES (1, 'ann@example.org')");

                // Act
                var rows = await RowsAsync(session,
                    "SELECT CAST(e AS email), CAST(CAST(' ANN@Example.org ' AS email) AS email), CAST(e AS TEXT), CAST(e COLLATE binary AS EMAIL) FROM c");
                await ExecuteAsync(session, "UPDATE c SET e = CAST(e AS email) WHERE id = 1");

                // Assert
                rows.ShouldBe([["ann@example.org", "ann@example.org", "masked", "ann@example.org"]]);
                (await RowsAsync(session, "SELECT e FROM c")).ShouldBe([["ann@example.org"]]);
            }

            // Act 2: an engine whose email check is stricter still checks a stored value cast to the domain.
            var strict = SqlType.CreateDomain("email", SqlType.VarChar(320),
                SqlScalarFunction.Create("is_dot_com", static (string value) => value.EndsWith(".com", StringComparison.Ordinal), SqlFunctionVolatility.Immutable));
            await using (var engine = await BuildAsync(types => types.Add(strict), root))
            {
                var database = await engine.OpenDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                var refused = await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(session, "SELECT CAST(e AS email) FROM c"));
                refused.ConstraintName.ShouldBe("email");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: every write to a domain column applies the base type's length, precision and scale, as CAST does")]
    public async Task DomainColumn_Writes_ShouldApplyTheBaseTypesModifiers()
    {
        // Arrange
        await using var engine = await BuildAsync(static types => types
            .Add(SqlType.CreateDomain("money2", SqlType.Decimal(5, 2)))
            .Add(SqlType.CreateDomain("code5", SqlType.VarChar(5))));
        var database = await engine.CreateDatabaseAsync("shop");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE m (id INT, v money2, c code5)");
        await ExecuteAsync(session, "INSERT INTO m VALUES (1, 123.45, 'ABCDE')");

        // Act
        var scale = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO m VALUES (2, 123.456, 'A')"));
        var precision = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO m VALUES (3, 1234.5, 'A')"));
        var length = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO m VALUES (4, 1, 'ABCDEF')"));
        var update = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE m SET c = 'ABCDEF' WHERE id = 1"));
        var select = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO m (id, v, c) SELECT id + 10, v * 10, c FROM m"));
        var added = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE m ADD COLUMN extra code5 DEFAULT 'TOOLONG'"));
        var cast = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST(1234.5 AS money2) FROM m"));

        // Assert: nothing was written, and the column and a CAST bound the value alike.
        scale.Message.ShouldBe("Column 'v' of table 'dbo.m' (domain 'money2', NUMERIC(5, 2)): Value exceeds scale 2; rounding is not supported.");
        precision.Message.ShouldBe("COHSQLE002: Numeric value out of range: Column 'v' of table 'dbo.m' (domain 'money2', NUMERIC(5, 2)): " +
            "Value exceeds precision 5 and scale 2.");
        length.Message.ShouldBe("Column 'c' of table 'dbo.m' (domain 'code5', VARCHAR(5)): String length exceeds 5; truncation is not supported.");
        update.Message.ShouldBe(length.Message);
        select.Message.ShouldStartWith("COHSQLE002: Numeric value out of range: Column 'v' of table 'dbo.m' (domain 'money2'", Case.Sensitive);
        added.Message.ShouldContain("String length exceeds 5", Case.Sensitive);
        cast.Message.ShouldContain("Value exceeds precision 5 and scale 2", Case.Sensitive);
        (await RowsAsync(session, "SELECT id, v, c FROM m")).ShouldBe([[1, 123.45m, "ABCDE"]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a domain over NUMERIC beyond what System.Decimal bounds casts and stores")]
    public async Task Domain_OverWideNumeric_ShouldCastAndStore()
    {
        // Arrange: precision 40 and scale 30 bound nothing System.Decimal can exceed.
        await using var engine = await BuildAsync(static types => types
            .Add(SqlType.CreateDomain("bignum", SqlType.Decimal(40, 2)))
            .Add(SqlType.CreateDomain("fine", SqlType.Decimal(40, 30))));
        var database = await engine.CreateDatabaseAsync("wide");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE b (id INT, v bignum, f fine)");

        // Act
        await ExecuteAsync(session, "INSERT INTO b VALUES (1, 12345678901234567890.5, 0.000000000000000000000000001)");
        var rows = await RowsAsync(session, "SELECT CAST(1 AS bignum), CAST('1.5' AS bignum), CAST('1.25' AS fine), v, f FROM b");
        var scale = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST('1.255' AS bignum) FROM b"));

        // Assert
        rows.ShouldBe([[1m, 1.5m, 1.25m, 12345678901234567890.5m, 0.000000000000000000000000001m]]);
        scale.Message.ShouldContain("Value exceeds scale 2", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: GROUP BY matches a CAST to a domain whether its name is quoted or not")]
    public async Task GroupBy_CastToADomainQuotedOrNot_ShouldMatch()
    {
        // Arrange
        await using var engine = await BuildAsync(static types => types.Add(Email()));
        var database = await engine.CreateDatabaseAsync("crm");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE child (id INT, owner TEXT)");
        await ExecuteAsync(session, "INSERT INTO child VALUES (1, 'a@x'), (2, 'a@x')");

        // Act
        var rows = await RowsAsync(session, "SELECT CAST(owner AS \"email\"), COUNT(*) FROM child GROUP BY CAST(owner AS email)");

        // Assert
        var row = rows.ShouldHaveSingleItem();
        row[0].ShouldBe("a@x");
        Convert.ToInt64(row[1], CultureInfo.InvariantCulture).ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a reopening engine whose domain changed its base length, precision or scale refuses writes with COHSQLE010")]
    public async Task Reopen_DomainWithOtherModifiers_ShouldRefuseWrites()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-domain-modifiers", Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange
            static void Original(SqlTypeCollection types) => types
                .Add(SqlType.CreateDomain("code", SqlType.VarChar(10)))
                .Add(SqlType.CreateDomain("amount", SqlType.Decimal(10, 2)));
            await using (var engine = await BuildAsync(Original, root))
            {
                var database = await engine.CreateDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "CREATE TABLE r (id INT, c code, a amount)");
                await ExecuteAsync(session, "INSERT INTO r VALUES (1, 'ABCDEFGHIJ', 12.5)");
            }

            // Act 1: the same registrations bind again, so the column's stored bound round-trips.
            await using (var engine = await BuildAsync(Original, root))
            {
                var database = await engine.OpenDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "INSERT INTO r VALUES (2, 'XYZ', 1.25)");
            }

            // Act 2: the same names over narrower base types do not bind.
            await using (var engine = await BuildAsync(static types => types
                .Add(SqlType.CreateDomain("code", SqlType.VarChar(3)))
                .Add(SqlType.CreateDomain("amount", SqlType.Decimal(4, 1))), root))
            {
                var database = await engine.OpenDatabaseAsync("crm");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                var rows = await RowsAsync(session, "SELECT id, c, a FROM r ORDER BY id");
                var insert = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO r VALUES (3, 'X', 1)"));
                var update = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE r SET a = 1 WHERE id = 1"));

                // Assert
                rows.ShouldBe([[1, "ABCDEFGHIJ", 12.5m], [2, "XYZ", 1.25m]]);
                insert.Message.ShouldStartWith($"{UnregisteredType}: Column 'c' of table 'dbo.r' is of domain 'code', which this engine registers over " +
                    "VARCHAR(3), but the column stores VARCHAR(10), so the column cannot be written", Case.Sensitive);
                update.Message.ShouldStartWith($"{UnregisteredType}: Column 'a' of table 'dbo.r' is of domain 'amount', which this engine registers over " +
                    "NUMERIC(4, 1), but the column stores NUMERIC(10, 2)", Case.Sensitive);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a CAST of a domain no cast converts names the domain while planning")]
    public async Task Cast_FromADomainWithoutACast_ShouldNameTheDomainWhilePlanning()
    {
        // Arrange
        var cents = SqlType.CreateDomain("cents", SqlType.BigInt);
        await using var engine = await BuildAsync(types => types.Add(cents));
        var database = await engine.CreateDatabaseAsync("shop");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE p (id INT, amount cents)");

        // Act
        var fromDomain = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST(amount AS DATE) FROM p"));
        var toDomain = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST(TRUE AS cents) FROM p"));

        // Assert
        fromDomain.Message.ShouldStartWith("COHSQLE011: Cannot CAST type cents to DATE: no cast from cents to DATE is registered.", Case.Sensitive);
        toDomain.Message.ShouldStartWith("COHSQLE011: Cannot CAST type BOOLEAN to cents: no cast from BOOLEAN to cents or to its base type BIGINT is registered.",
            Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domains: a cast that throws past a value's fault is COHSQLE007, and only the statement's cancellation cancels")]
    public async Task Cast_ThrowingApplicationCast_ShouldBeCodedUnlessTheStatementIsCanceled()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        var points = SqlType.CreateDomain("points", SqlType.BigInt);
        await using var engine = await BuildAsync(types => types.Add(points).AddCast(SqlCast.Create<string, long>(SqlType.Text, points, text => text switch
        {
            "npe" => throw new NullReferenceException("npe!"),
            "oce" => throw new TaskCanceledException("not this statement"),
            "bad" => throw new FormatException("not a number of points"),
            "cancel" => CancelThenThrow(cancel),
            _ => long.Parse(text, CultureInfo.InvariantCulture),
        }, SqlCastContext.Explicit)));
        var database = await engine.CreateDatabaseAsync("scores");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");

        // Act
        var npe = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST('npe' AS points) FROM t"));
        var oce = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST('oce' AS points) FROM t"));
        var bad = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT CAST('bad' AS points) FROM t"));
        var canceled = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await using var result = (QueryResultSet)await session.ExecuteAsync("SELECT CAST('cancel' AS points) FROM t", null, cancel.Token);
            await ReadAsync(result);
        });
        var after = await RowsAsync(session, "SELECT CAST('7' AS points) FROM t");

        // Assert: the value's fault keeps the conversion contract's message; the code's fault is coded.
        npe.Message.ShouldBe("COHSQLE007: Cast TEXT AS points failed: npe!");
        npe.InnerException.ShouldBeOfType<NullReferenceException>();
        oce.Message.ShouldBe("COHSQLE007: Cast TEXT AS points failed: not this statement");
        oce.InnerException.ShouldBeOfType<TaskCanceledException>();
        bad.Message.ShouldBe("CAST from String to points failed: not a number of points");
        bad.InnerException.ShouldBeOfType<FormatException>();
        after.ShouldBe([[7L]]);

        static long CancelThenThrow(CancellationTokenSource source)
        {
            source.Cancel();
            throw new OperationCanceledException(source.Token);
        }
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
