using System;
using System.IO;
using System.Linq;
using System.Security;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public class DatabaseModelTargetsTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Targets: schema artifacts use the TFM intermediate path")]
    public void Evaluate_WithTargetFrameworkIntermediatePath_ShouldUseItForSchemaArtifacts()
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        Project project = LoadProject(directory, projects, "Sql");

        project.GetPropertyValue("CohesionDatabaseSchemaOutputDirectory")
            .Replace('\\', '/')
            .ShouldBe("obj/Debug/net10.0/cohesion/database/");
        project.GetPropertyValue("_CohesionDatabaseSchemaManifest")
            .Replace('\\', '/')
            .ShouldBe("obj/Debug/net10.0/cohesion/database/schemas.manifest");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Targets: the manifest in the artifact directory is the compile target's incremental output")]
    [InlineData("Sql", "_CohesionCompileSqlDatabaseSchema")]
    [InlineData("KeyValuePair", "_CohesionCompileKeyValuePairDatabaseSchema")]
    public void Evaluate_ModelCompileTarget_ShouldUseTheManifestAsItsOutput(string model, string compileTarget)
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        Project project = LoadProject(directory, projects, model);

        // A deleted directory, a deleted artifact (the check target deletes the manifest) or a new
        // output directory each leave the target out of date, so the artifacts are written again.
        project.Targets[compileTarget].Outputs.ShouldBe("$(_CohesionDatabaseSchemaManifest)");
        project.Targets[compileTarget].Children.OfType<ProjectTaskInstance>().ShouldNotContain(task => task.Name == "Touch");
        string[] dependencies = project.Targets["CohesionDatabaseCompileSchema"].DependsOnTargets
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.IndexOf(dependencies, "_CohesionDatabaseCheckSchemaArtifacts")
            .ShouldBeLessThan(Array.IndexOf(dependencies, "$(_CohesionDatabaseModelCompileTarget)"));
        project.Targets["_CohesionDatabaseCheckSchemaArtifacts"].Children.OfType<ProjectTaskInstance>()
            .ShouldContain(task => task.Name == "Delete" && task.Parameters["Files"] == "$(_CohesionDatabaseSchemaManifest)");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Targets: a migration names its database and reads the artifact directory")]
    public void Evaluate_CreateMigrationTarget_ShouldPassTheDatabaseNameAndArtifactDirectory()
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        Project project = LoadProject(directory, projects, "Sql");

        ProjectTaskInstance migration = project.Targets["CohesionDatabaseCreateMigration"]
            .Children
            .OfType<ProjectTaskInstance>()
            .Single(task => task.Name == "CreateDatabaseMigrationTask");
        migration.Parameters["SchemaDirectory"].ShouldBe("$(CohesionDatabaseSchemaOutputDirectory)");
        migration.Parameters["DatabaseName"].ShouldBe("$(CohesionDatabaseName)");
        migration.Parameters["MigrationsRoot"].ShouldBe("$(CohesionDatabaseMigrationsRoot)");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Targets: exact model imports its compile tool set")]
    [InlineData("Sql", "_CohesionCompileSqlDatabaseSchema")]
    [InlineData("KeyValuePair", "_CohesionCompileKeyValuePairDatabaseSchema")]
    public void Evaluate_WithKnownModel_ShouldImportOnlyItsToolSet(string model, string expectedTarget)
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        Project project = LoadProject(directory, projects, model);

        project.GetPropertyValue("_CohesionDatabaseModelTargetsLoaded").ShouldBe("true");
        project.GetPropertyValue("_CohesionDatabaseModelCompileTarget").ShouldBe(expectedTarget);
        project.Targets.ContainsKey(expectedTarget).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Targets: unknown or case-mismatched model fails validation")]
    [InlineData("sql")]
    [InlineData("Documents")]
    public void Evaluate_WithUnknownModel_ShouldExposeNamedError(string model)
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        Project project = LoadProject(directory, projects, model);

        project.GetPropertyValue("_CohesionDatabaseModelTargetsLoaded").ShouldBeEmpty();
        project.GetPropertyValue("_CohesionDatabaseModelCompileTarget").ShouldBeEmpty();
        project.Targets.ContainsKey("_CohesionCompileSqlDatabaseSchema").ShouldBeFalse();
        project.Targets.ContainsKey("_CohesionCompileKeyValuePairDatabaseSchema").ShouldBeFalse();
        ProjectTaskInstance diagnostic = project.Targets["_CohesionDatabaseValidateModel"]
            .Children
            .OfType<ProjectTaskInstance>()
            .Single(task => task.Name == "Error" && task.Parameters["Code"] == "COHDBSDK001");
        diagnostic.Parameters["Text"].ShouldContain("case-sensitive");
    }

    private static Project LoadProject(
        TemporaryDirectory directory,
        ProjectCollection projects,
        string model)
    {
        // bin/<cfg>/<tfm> -> tests -> Tasks -> the SDK family root, which is where Targets/ sits.
        string targetsDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Targets"));
        string propsPath = Path.Combine(targetsDirectory, "Sdk.Database.props");
        string targetPath = Path.Combine(targetsDirectory, "Assimalign.Cohesion.Sdk.Database.Migration.targets");
        File.Exists(propsPath).ShouldBeTrue($"Expected Database SDK props at '{propsPath}'.");
        File.Exists(targetPath).ShouldBeTrue($"Expected Database SDK targets at '{targetPath}'.");
        string projectPath = directory.File("TargetEvaluation.proj");
        File.WriteAllText(projectPath, $$"""
            <Project>
              <PropertyGroup>
                <CohesionDatabaseProject>true</CohesionDatabaseProject>
                <CohesionDatabaseModel>{{SecurityElement.Escape(model)}}</CohesionDatabaseModel>
              </PropertyGroup>
              <Import Project="{{SecurityElement.Escape(propsPath)}}" />
              <PropertyGroup>
                <IntermediateOutputPath>obj/Debug/net10.0/</IntermediateOutputPath>
              </PropertyGroup>
              <Import Project="{{SecurityElement.Escape(targetPath)}}" />
            </Project>
            """);
        return new Project(projectPath, globalProperties: null, toolsVersion: null, projectCollection: projects);
    }

}
