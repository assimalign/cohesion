using System;
using System.IO;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Sdk.Database.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public class CreateDatabaseMigrationTaskTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: v8 FK artifacts and baselines require rebuilding (#1723)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_V8ForeignKeyDocument_ShouldRefuseFormat(bool baseline)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var current = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new("c", "Child", [new("id", DatabaseType.Int32, true)], null, [],
                [new("fk", CompiledSchemaConstraintKind.Reference, ["id"], "p", ["id"])]),
             new("p", "Parent", [new("id", DatabaseType.Int32, false)], new("pk", ["id"]), [], [])], []);
        string artifact = WriteSchema(directory.File("schemas"), current);
        string document = current.CanonicalDocument.Replace(SqlCompiledSchema.CurrentFormat, "cohesion/database-schema/v8", StringComparison.Ordinal)
            .Replace(",\"onUpdate\":2", "", StringComparison.Ordinal);
        if (baseline)
        {
            Directory.CreateDirectory(directory.File("Migrations/orders"));
            artifact = directory.File("Migrations/orders/0001_initial.schema.json");
        }
        File.WriteAllText(artifact, document);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document)));
        File.WriteAllText(Path.ChangeExtension(artifact, ".sha256"), hash);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, engine, "next");

        // Act
        task.Execute().ShouldBeFalse();

        // Assert
        var error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBSDK206");
        error.Message.ShouldNotBeNull().ShouldContain("schema.format", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v8", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v11", Case.Sensitive);
        task.MigrationPath.ShouldBeEmpty();
        task.BaselinePath.ShouldBeEmpty();
        File.ReadAllText(artifact).ShouldBe(document);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: v4 numeric artifacts and baselines reach the format gate")]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_V4NumericDocument_ShouldRefuseFormat(bool baseline)
    {
        // Arrange: preserve the pre-normalization v4 document and hash its actual bytes.
        using var directory = new TemporaryDirectory();
        var current = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new CompiledSchemaTable("items", "Tests.Row",
                [new CompiledSchemaColumn("value", DatabaseType.Decimal, true, Precision: 3, CustomType: "NUMERIC(3)")],
                null, [], [])], []);
        string artifact = WriteSchema(directory.File("schemas"), current);
        string document = current.CanonicalDocument
            .Replace(SqlCompiledSchema.CurrentFormat, "cohesion/database-schema/v4", StringComparison.Ordinal)
            .Replace("\"scale\":0", "\"scale\":null", StringComparison.Ordinal);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document)));
        if (baseline)
        {
            Directory.CreateDirectory(directory.File("Migrations/orders"));
            artifact = directory.File("Migrations/orders/0001_initial.schema.json");
        }
        File.WriteAllText(artifact, document);
        File.WriteAllText(Path.ChangeExtension(artifact, ".sha256"), hash);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, engine, "next");

        // Act
        bool result = task.Execute();

        // Assert
        result.ShouldBeFalse();
        var error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBSDK206");
        error.Message.ShouldNotBeNull().ShouldContain("schema.format", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v4", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v11", Case.Sensitive);
        error.Message.ShouldContain("Rebuild", Case.Sensitive);
        task.MigrationPath.ShouldBeEmpty();
        task.BaselinePath.ShouldBeEmpty();
        File.ReadAllText(artifact).ShouldBe(document);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: v7 temporal artifacts and baselines reach the format gate without changing files")]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_V7TemporalDocument_ShouldRefuseFormatWithoutChangingFiles(bool baseline)
    {
        // Arrange: retain the v7 TimeSpan spelling and hash the artifact's actual bytes.
        using var directory = new TemporaryDirectory();
        var current = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new CompiledSchemaTable("items", "Tests.Row",
                [new CompiledSchemaColumn("duration", DatabaseType.TimeSpan, true, DefaultLiteral: "'93784.0000005 seconds'")],
                null, [], [])], []);
        string artifact = WriteSchema(directory.File("schemas"), current);
        string document = current.CanonicalDocument
            .Replace(SqlCompiledSchema.CurrentFormat, "cohesion/database-schema/v7", StringComparison.Ordinal)
            .Replace("93784.0000005 seconds", "1.02:03:04.0000005", StringComparison.Ordinal);
        if (baseline)
        {
            Directory.CreateDirectory(directory.File("Migrations/orders"));
            File.WriteAllText(directory.File("Migrations/orders/0001_initial.schema.json"), document);
        }
        else
        {
            File.WriteAllText(artifact, document);
        }
        string artifactHash = baseline ? current.Hash : Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document)));
        File.WriteAllText(Path.ChangeExtension(artifact, ".sha256"), artifactHash + "\n");
        string[] inputPaths = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        byte[][] inputContents = inputPaths.Select(File.ReadAllBytes).ToArray();
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, engine, "next");

        // Act
        bool result = task.Execute();

        // Assert
        result.ShouldBeFalse();
        var error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBSDK206");
        error.Message.ShouldNotBeNull().ShouldContain("schema.format", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v7", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v11", Case.Sensitive);
        error.Message.ShouldContain("Rebuild", Case.Sensitive);
        task.MigrationPath.ShouldBeEmpty();
        task.BaselinePath.ShouldBeEmpty();
        Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ShouldBe(inputPaths);
        for (int i = 0; i < inputPaths.Length; i++)
        {
            File.ReadAllBytes(inputPaths[i]).ShouldBe(inputContents[i]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: SQL writes ordinal script and baseline")]
    public void Execute_WithSqlSchema_ShouldWriteOrdinalScriptAndBaseline()
    {
        using var directory = new TemporaryDirectory();
        string schemaDirectory = directory.File("schemas");
        string schemaPath = WriteSchema(schemaDirectory, CreateSchema(includeDescription: false));
        var engine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, engine, "initial");

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        Path.GetFileName(Path.GetDirectoryName(task.MigrationPath)).ShouldBe("orders");
        Path.GetFileName(task.MigrationPath).ShouldBe("0001_initial.sql");
        Path.GetFileName(task.BaselinePath).ShouldBe("0001_initial.schema.json");
        string script = File.ReadAllText(task.MigrationPath);
        script.ShouldContain("CREATE TABLE IF NOT EXISTS dbo.Orders (Id BIGINT PRIMARY KEY NOT NULL);");
        script.ShouldNotContain("BEGIN;");
        script.ShouldNotContain("COMMIT;");
        SqlCompiledSchemaSerializer.Read(task.BaselinePath).Hash.ShouldBe(CreateSchema(includeDescription: false).Hash);

        SqlCompiledSchemaSerializer.Write(schemaPath, CreateSchema(includeDescription: true));
        task.MigrationName = "add-description";
        task.Execute().ShouldBeTrue();
        Path.GetFileName(task.MigrationPath).ShouldBe("0002_add-description.sql");
        File.ReadAllText(task.MigrationPath).ShouldContain("ALTER TABLE dbo.Orders ADD COLUMN Description TEXT NULL;");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: each database numbers its own migrations")]
    public void Execute_WithTwoDatabases_ShouldNumberEachInItsOwnFolder()
    {
        using var directory = new TemporaryDirectory();
        string schemaDirectory = directory.File("schemas");
        WriteSchema(schemaDirectory, CreateSchema(includeDescription: false));
        WriteSchema(schemaDirectory, CreateStringKeySchema());
        var engine = new RecordingBuildEngine();

        CreateDatabaseMigrationTask orders = CreateTask(directory, engine, "initial");
        orders.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));
        CreateDatabaseMigrationTask sessions = CreateTask(directory, engine, "initial", "SESSIONS");
        sessions.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        Path.GetRelativePath(directory.Path, orders.MigrationPath).Replace('\\', '/').ShouldBe("Migrations/orders/0001_initial.sql");
        Path.GetRelativePath(directory.Path, sessions.MigrationPath).Replace('\\', '/').ShouldBe("Migrations/sessions/0001_initial.sql");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: a database renamed only in case keeps its migrations folder and numbering (#1392)")]
    public void Execute_WithFolderOfAnotherCase_ShouldContinueItsNumbering()
    {
        // Arrange: the history of 'orders' in a folder spelled 'Orders', as a declaration of 'Orders'
        // left it before it was renamed 'orders'.
        using var directory = new TemporaryDirectory();
        string history = directory.File("Migrations/Orders");
        Directory.CreateDirectory(history);
        SqlCompiledSchemaSerializer.Write(Path.Combine(history, "0001_initial.schema.json"), CreateSchema(includeDescription: false));
        WriteSchema(directory.File("schemas"), CreateSchema(includeDescription: true));
        var engine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, engine, "add-description");

        // Act
        bool created = task.Execute();

        // Assert: the next ordinal, in the existing folder, by its exact name, which is what a
        // case-sensitive file system sees.
        created.ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));
        Path.GetRelativePath(directory.Path, task.MigrationPath).Replace('\\', '/').ShouldBe("Migrations/Orders/0002_add-description.sql");
        Directory.EnumerateDirectories(directory.File("Migrations")).Select(Path.GetFileName).ShouldBe(["Orders"]);
        File.ReadAllText(task.MigrationPath).ShouldContain("ALTER TABLE dbo.Orders ADD COLUMN Description TEXT NULL;");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: two folders that match one database ignoring case are refused (#1392)")]
    public void Execute_WithTwoFoldersOfOneDatabase_ShouldFailWithoutFiles()
    {
        // Arrange: 'Orders' and 'orders', which only a case-sensitive file system holds apart; one
        // that folds case makes the second the first.
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.File("Migrations/Orders"));
        Directory.CreateDirectory(directory.File("Migrations/orders"));
        WriteSchema(directory.File("schemas"), CreateSchema(includeDescription: false));
        string[] folders = Directory.EnumerateDirectories(directory.File("Migrations")).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        var engine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, engine, "initial");

        // Act
        bool created = task.Execute();

        // Assert: where the file system holds both, refused without a file written; where it holds
        // one, that folder takes the migration.
        if (folders.Length == 2)
        {
            created.ShouldBeFalse();
            engine.Errors.ShouldContain(error =>
                error.Code == "COHDBSDK208" &&
                error.Message != null &&
                error.Message.Contains("more than one migrations folder (Orders, orders)", StringComparison.Ordinal));
            Directory.EnumerateFiles(directory.File("Migrations"), "*", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        else
        {
            folders.ShouldBe(["Orders"]);
            created.ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));
            Path.GetRelativePath(directory.Path, task.MigrationPath).Replace('\\', '/').ShouldBe("Migrations/Orders/0001_initial.sql");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: the database must be named and declared")]
    public void Execute_WithoutDeclaredDatabaseName_ShouldFailNamingTheDeclaredDatabases()
    {
        using var directory = new TemporaryDirectory();
        string schemaDirectory = directory.File("schemas");
        WriteSchema(schemaDirectory, CreateSchema(includeDescription: false));
        WriteSchema(schemaDirectory, CreateStringKeySchema());
        var engine = new RecordingBuildEngine();

        CreateTask(directory, engine, "initial", databaseName: "").Execute().ShouldBeFalse();
        CreateTask(directory, engine, "initial", databaseName: "ledger").Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK207" &&
            error.Message != null &&
            error.Message.Contains("(declared: orders, sessions)", StringComparison.Ordinal));
        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK203" &&
            error.Message != null &&
            error.Message.Contains("database 'ledger' does not exist (declared: orders, sessions)", StringComparison.Ordinal));
        Directory.Exists(directory.File("Migrations")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: a project that declares one database needs no database name")]
    public void Execute_WithOneDeclaredDatabaseAndNoName_ShouldUseIt()
    {
        using var directory = new TemporaryDirectory();
        string schemaDirectory = directory.File("schemas");
        WriteSchema(schemaDirectory, CreateSchema(includeDescription: false));
        var engine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, engine, "initial", databaseName: "");

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        Path.GetRelativePath(directory.Path, task.MigrationPath).Replace('\\', '/').ShouldBe("Migrations/orders/0001_initial.sql");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: a schema document the manifest does not list is not a declared database")]
    public void Execute_WithForeignSchemaDocument_ShouldNotTreatItAsDeclared()
    {
        using var directory = new TemporaryDirectory();
        string schemaDirectory = directory.File("schemas");
        WriteSchema(schemaDirectory, CreateSchema(includeDescription: false));
        File.WriteAllText(Path.Combine(schemaDirectory, "appsettings.schema.json"), "{}");
        var engine = new RecordingBuildEngine();

        CreateTask(directory, engine, "initial", databaseName: "appsettings").Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK203" &&
            error.Message != null &&
            error.Message.Contains("database 'appsettings' does not exist (declared: orders)", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: key-value model fails explicitly")]
    public void Execute_WithKeyValuePairModel_ShouldFailExplicitly()
    {
        using var directory = new TemporaryDirectory();
        var engine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, engine, "initial");
        task.Model = "KeyValuePair";

        task.Execute().ShouldBeFalse();
        engine.Errors.ShouldContain(error => error.Code == "COHDBSDK201");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: reference primary key is forced non-null")]
    public void Execute_WithNullableReferencePrimaryKey_ShouldEmitNotNull()
    {
        using var directory = new TemporaryDirectory();
        WriteSchema(directory.File("schemas"), CreateStringKeySchema());
        var engine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, engine, "initial", "sessions");

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        File.ReadAllText(task.MigrationPath)
            .ShouldContain("CREATE TABLE IF NOT EXISTS dbo.Sessions (Id TEXT PRIMARY KEY NOT NULL);");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: required column without backfill fails")]
    public void Execute_WithRequiredColumnAddition_ShouldFailWithoutFiles()
    {
        using var directory = new TemporaryDirectory();
        string migrationsRoot = directory.File("Migrations/orders");
        Directory.CreateDirectory(migrationsRoot);
        SqlCompiledSchemaSerializer.Write(
            Path.Combine(migrationsRoot, "0001_initial.schema.json"),
            CreateSchema(includeDescription: false));
        WriteSchema(directory.File("schemas"), CreateSchema(includeDescription: true, descriptionIsNullable: false));
        var engine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, engine, "required-description");

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error => error.Code == "COHDBSDK206");
        File.Exists(Path.Combine(migrationsRoot, "0002_required-description.sql")).ShouldBeFalse();
        File.Exists(Path.Combine(migrationsRoot, "0002_required-description.schema.json")).ShouldBeFalse();
    }

    private static CreateDatabaseMigrationTask CreateTask(
        TemporaryDirectory directory,
        RecordingBuildEngine engine,
        string migrationName,
        string databaseName = "orders")
        => new()
        {
            BuildEngine = engine,
            SchemaDirectory = directory.File("schemas"),
            DatabaseName = databaseName,
            MigrationsRoot = directory.File("Migrations"),
            MigrationName = migrationName,
            Model = "Sql",
            ProjectDirectory = directory.Path
        };

    // Writes a schema artifact as the compile task does: the document, and its name in the manifest.
    private static string WriteSchema(string schemaDirectory, SqlCompiledSchema schema)
    {
        Directory.CreateDirectory(schemaDirectory);
        string path = Path.Combine(schemaDirectory, schema.Name + ".schema.json");
        SqlCompiledSchemaSerializer.Write(path, schema);
        File.AppendAllText(Path.Combine(schemaDirectory, "schemas.manifest"), schema.Name + "\n");
        return path;
    }

    private static SqlCompiledSchema CreateSchema(bool includeDescription, bool descriptionIsNullable = true)
    {
        CompiledSchemaColumn[] columns = includeDescription
            ? [
                new CompiledSchemaColumn("Id", DatabaseType.Int64, false),
                new CompiledSchemaColumn("Description", DatabaseType.String, descriptionIsNullable)]
            : [new CompiledSchemaColumn("Id", DatabaseType.Int64, false)];
        var table = new CompiledSchemaTable(
            "Orders",
            "Tests.Order",
            columns,
            new CompiledSchemaKey("PK_Orders", ["Id"]),
            [],
            []);
        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            "orders",
            false,
            [],
            [table],
            []);
    }

    private static SqlCompiledSchema CreateStringKeySchema()
    {
        var table = new CompiledSchemaTable(
            "Sessions",
            "Tests.Session",
            [new CompiledSchemaColumn("Id", DatabaseType.String, true)],
            new CompiledSchemaKey("PK_Sessions", ["Id"]),
            [],
            []);
        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            "sessions",
            false,
            [],
            [table],
            []);
    }
}
