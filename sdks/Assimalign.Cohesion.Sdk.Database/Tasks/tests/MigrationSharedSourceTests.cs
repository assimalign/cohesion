using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public sealed class MigrationSharedSourceTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Shared schema modules: linked helpers retain no process-global state")]
    [InlineData("Defaults")]
    [InlineData("MigrationLowering")]
    public void SharedSource_SchemaModule_ShouldBeLinkSafe(string module)
    {
        // Arrange
        string root = RepositoryRoot();
        string directory = Path.Combine(root, "resources/Database/Assimalign.Cohesion.Database.Sql.Schema/shared", module);
        var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);

        // Act / Assert: inspect every file, including future additions to the selected module.
        files.ShouldNotBeEmpty();
        foreach (string file in files)
        {
            IsLinkSafe(File.ReadAllText(file)).ShouldBeTrue(file);
        }
        string[] consumers = module == "MigrationLowering"
            ? ["resources/Database/Assimalign.Cohesion.Database.Sql/src/Assimalign.Cohesion.Database.Sql.csproj",
               "sdks/Assimalign.Cohesion.Sdk.Database/Tasks/src/Assimalign.Cohesion.Sdk.Database.Tasks.csproj"]
            : ["resources/Database/Assimalign.Cohesion.Database.Sql.Schema/src/Assimalign.Cohesion.Database.Sql.Schema.csproj",
               "sdks/Assimalign.Cohesion.Sdk.Database/Tasks/src/Assimalign.Cohesion.Sdk.Database.Tasks.csproj"];
        foreach (string consumer in consumers)
        {
            XDocument.Load(Path.Combine(root, consumer)).Descendants("CohesionSharedSource").ShouldContain(item =>
                (string?)item.Attribute("Include") == "Assimalign.Cohesion.Database.Sql.Schema" && (string?)item.Attribute("Module") == module);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Shared source guard: mutable state and identity-bearing objects are refused")]
    [InlineData("internal static class Helper { private static int _counter; }")]
    [InlineData("internal static class Helper { private static readonly object _sync = new(); }")]
    [InlineData("internal static class Helper { internal static object Instance { get; } = new(); }")]
    [InlineData("internal class Helper { }")]
    [InlineData("internal static class Helper { internal static event System.Action? Changed; }")]
    [InlineData("internal static class Helper { internal static void Run() { lock (new object()) { } } }")]
    public void IsLinkSafe_StatefulSource_ShouldRefuse(string source)
    {
        // Arrange / Act / Assert
        IsLinkSafe(source).ShouldBeFalse();
    }

    private static bool IsLinkSafe(string source)
    {
        var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
        return syntax.DescendantNodes().OfType<TypeDeclarationSyntax>().All(type => type is ClassDeclarationSyntax && type.Modifiers.Any(SyntaxKind.StaticKeyword) && type.BaseList is null) &&
            syntax.DescendantNodes().OfType<FieldDeclarationSyntax>().All(field => field.Modifiers.Any(SyntaxKind.ConstKeyword)) &&
            syntax.DescendantNodes().OfType<PropertyDeclarationSyntax>().All(property => property.Modifiers.Any(SyntaxKind.StaticKeyword) && property.ExpressionBody is not null) &&
            !syntax.DescendantNodes().Any(node => node is LockStatementSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../.."));
}
