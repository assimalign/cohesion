using System;
using System.IO;
using System.Linq;

using Assimalign.Cohesion.Database;
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
        string schemaPath = directory.File("database.schema.json");
        SqlCompiledSchemaSerializer.Write(schemaPath, CreateSchema(includeDescription: false));
        var engine = new RecordingBuildEngine();
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = engine,
            SchemaModelPath = schemaPath,
            MigrationsRoot = directory.File("Migrations"),
            MigrationName = "initial",
            Model = "Sql",
            ProjectDirectory = directory.Path
        };

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

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

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: key-value model fails explicitly")]
    public void Execute_WithKeyValuePairModel_ShouldFailExplicitly()
    {
        using var directory = new TemporaryDirectory();
        var engine = new RecordingBuildEngine();
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = engine,
            SchemaModelPath = directory.File("unused.json"),
            MigrationsRoot = directory.File("Migrations"),
            MigrationName = "initial",
            Model = "KeyValuePair",
            ProjectDirectory = directory.Path
        };

        task.Execute().ShouldBeFalse();
        engine.Errors.ShouldContain(error => error.Code == "COHDBSDK201");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: reference primary key is forced non-null")]
    public void Execute_WithNullableReferencePrimaryKey_ShouldEmitNotNull()
    {
        using var directory = new TemporaryDirectory();
        string schemaPath = directory.File("database.schema.json");
        SqlCompiledSchemaSerializer.Write(schemaPath, CreateStringKeySchema());
        var engine = new RecordingBuildEngine();
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = engine,
            SchemaModelPath = schemaPath,
            MigrationsRoot = directory.File("Migrations"),
            MigrationName = "initial",
            Model = "Sql",
            ProjectDirectory = directory.Path
        };

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        File.ReadAllText(task.MigrationPath)
            .ShouldContain("CREATE TABLE IF NOT EXISTS dbo.Sessions (Id TEXT PRIMARY KEY NOT NULL);");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Create migration: required column without backfill fails")]
    public void Execute_WithRequiredColumnAddition_ShouldFailWithoutFiles()
    {
        using var directory = new TemporaryDirectory();
        string schemaPath = directory.File("database.schema.json");
        string migrationsRoot = directory.File("Migrations");
        Directory.CreateDirectory(migrationsRoot);
        SqlCompiledSchemaSerializer.Write(
            Path.Combine(migrationsRoot, "0001_initial.schema.json"),
            CreateSchema(includeDescription: false));
        SqlCompiledSchemaSerializer.Write(
            schemaPath,
            CreateSchema(includeDescription: true, descriptionIsNullable: false));
        var engine = new RecordingBuildEngine();
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = engine,
            SchemaModelPath = schemaPath,
            MigrationsRoot = migrationsRoot,
            MigrationName = "required-description",
            Model = "Sql",
            ProjectDirectory = directory.Path
        };

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error => error.Code == "COHDBSDK206");
        File.Exists(Path.Combine(migrationsRoot, "0002_required-description.sql")).ShouldBeFalse();
        File.Exists(Path.Combine(migrationsRoot, "0002_required-description.schema.json")).ShouldBeFalse();
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
            EngineModel.Sql,
            false,
            [],
            [table],
            [],
            [],
            [],
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
            EngineModel.Sql,
            false,
            [],
            [table],
            [],
            [],
            [],
            []);
    }
}
