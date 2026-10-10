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
