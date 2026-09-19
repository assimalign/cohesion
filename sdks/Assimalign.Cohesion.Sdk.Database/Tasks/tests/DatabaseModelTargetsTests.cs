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

        project.GetPropertyValue("CohesionDatabaseSchemaOutputPath")
            .Replace('\\', '/')
            .ShouldBe("obj/Debug/net10.0/cohesion/database.schema.json");
        project.GetPropertyValue("CohesionDatabaseSchemaHashOutputPath")
            .Replace('\\', '/')
            .ShouldBe("obj/Debug/net10.0/cohesion/database.schema.sha256");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Targets: mapper generation is compiler-visible without an implicit opt-in")]
    public void Evaluate_WithoutMappingOptIn_ShouldExposePropertyWithoutEnablingGeneration()
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        Project project = LoadProject(directory, projects, "Sql");

        project.GetItems("CompilerVisibleProperty")
            .Select(item => item.EvaluatedInclude)
            .ShouldContain("CohesionGenerateDatabaseMappers");
        project.GetPropertyValue("CohesionGenerateDatabaseMappers").ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Targets: consumer controls mapper generation")]
    [InlineData("true")]
    [InlineData("false")]
    public void Evaluate_WithMappingSetting_ShouldPreserveConsumerChoice(string mappingSetting)
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        Project project = LoadProject(directory, projects, "Sql", mappingSetting);

        project.GetItems("CompilerVisibleProperty")
            .Select(item => item.EvaluatedInclude)
            .ShouldContain("CohesionGenerateDatabaseMappers");
        project.GetPropertyValue("CohesionGenerateDatabaseMappers").ShouldBe(mappingSetting);
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Framework: Database targeting pack delivers the mapper generator")]
    public void Evaluate_DatabaseFramework_ShouldIncludeRuntimeAndCompilerContracts()
    {
        using var directory = new TemporaryDirectory();
        using var projects = new ProjectCollection();
        string manifestPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "..", "frameworks", "Assimalign.Cohesion.App.props"));
        File.Exists(manifestPath).ShouldBeTrue();
        string projectPath = directory.File("FrameworkEvaluation.proj");
        File.WriteAllText(projectPath, $$"""
            <Project>
              <PropertyGroup>
                <CohesionFrameworkName>Assimalign.Cohesion.App.Database</CohesionFrameworkName>
              </PropertyGroup>
              <Import Project="{{SecurityElement.Escape(manifestPath)}}" />
            </Project>
            """);

        var project = new Project(projectPath, globalProperties: null, toolsVersion: null, projectCollection: projects);

        project.GetItems("CohesionFrameworkAssembly").Select(item => item.EvaluatedInclude)
            .ShouldContain("Assimalign.Cohesion.Database.Mapping");
        project.GetItems("CohesionFrameworkAnalyzer").Select(item => item.EvaluatedInclude)
            .ShouldContain("Assimalign.Cohesion.SourceGeneration.Database");
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
        string model,
        string? mappingSetting = null)
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
                <CohesionGenerateDatabaseMappers>{{SecurityElement.Escape(mappingSetting ?? string.Empty)}}</CohesionGenerateDatabaseMappers>
              </PropertyGroup>
              <Import Project="{{SecurityElement.Escape(targetPath)}}" />
            </Project>
            """);
        return new Project(projectPath, globalProperties: null, toolsVersion: null, projectCollection: projects);
    }

}
