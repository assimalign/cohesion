using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        const string owner = "Assimalign.Cohesion.Database.Sql.Schema";
        string directory = Path.Combine(root, "resources/Database", owner, "shared", module);
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
                (string?)item.Attribute("Include") == owner && (string?)item.Attribute("Module") == module);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Shared source guard: mutable state and identity-bearing objects are refused")]
    [InlineData("internal static class Helper { private static int _counter; }")]
    [InlineData("internal static class Helper { private static readonly object _sync = new(); }")]
    [InlineData("internal static class Helper { internal static object Instance { get; } = new(); }")]
    [InlineData("internal class Helper { }")]
    [InlineData("internal static class Helper { internal static event System.Action? Changed; }")]
    [InlineData("internal static class Helper { internal static void Run() { lock (new object()) { } } }")]
    [InlineData("internal static class Helper { static Helper() { System.Console.WriteLine(1); } }")]
    [InlineData("internal static class Helper { [System.Runtime.CompilerServices.ModuleInitializer] internal static void Init() { } }")]
    [InlineData("internal static class Helper { [global::System.Runtime.CompilerServices.ModuleInitializerAttribute] internal static void Init() { } }")]
    [InlineData("using Init = System.Runtime.CompilerServices.ModuleInitializerAttribute; internal static class Helper { [Init] internal static void Run() { } }")]
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
            !syntax.DescendantNodes().Any(node => node is LockStatementSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax or
                ConstructorDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax) && IsInitializationFree(syntax);
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Shared modules: every SDK and SQL link has no static constructors or module initializers")]
    public void SharedSource_AllModules_ShouldHaveNoInitializers()
    {
        // Arrange: derive modules from both consumers so a new link cannot escape the guard.
        string root = RepositoryRoot();
        string[] consumers =
        [
            "sdks/Assimalign.Cohesion.Sdk.Database/Tasks/src/Assimalign.Cohesion.Sdk.Database.Tasks.csproj",
            "resources/Database/Assimalign.Cohesion.Database.Sql/src/Assimalign.Cohesion.Database.Sql.csproj",
        ];
        var modules = consumers.SelectMany(consumer => XDocument.Load(Path.Combine(root, consumer)).Descendants("CohesionSharedSource"))
            .Select(item => (Owner: (string?)item.Attribute("Include"), Module: (string?)item.Attribute("Module"))).Distinct().ToArray();

        // Act / Assert: static field/property initializers are allowed outside the stricter schema modules.
        modules.ShouldNotBeEmpty();
        foreach (var module in modules)
        {
            module.Owner.ShouldNotBeNullOrWhiteSpace();
            module.Module.ShouldNotBeNullOrWhiteSpace();
            var owners = new[] { "libraries", "resources" }.SelectMany(ownerRoot =>
                Directory.EnumerateFiles(Path.Combine(root, ownerRoot), module.Owner + ".csproj", SearchOption.AllDirectories)).ToArray();
            owners.Length.ShouldBe(1, module.Owner);
            string directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(owners[0])!, "..", "shared", module.Module));
            Directory.Exists(directory).ShouldBeTrue(directory);
            var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
            files.ShouldNotBeEmpty(directory);
            var sources = files.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot()).ToArray();
            IsInitializationFree(sources).ShouldBeTrue(directory);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Initializer guard: static constructors and module initializers are refused")]
    [InlineData("internal static class Helper { static Helper() { } }")]
    [InlineData("using Init = System.Runtime.CompilerServices.ModuleInitializerAttribute; internal static class Helper { [Init] internal static void Run() { } }")]
    [InlineData("using InitAttribute = System.Runtime.CompilerServices.ModuleInitializerAttribute; internal static class Helper { [Init] internal static void Run() { } }")]
    public void IsInitializationFree_InitializerSource_ShouldRefuse(string source)
    {
        // Arrange / Act / Assert
        IsInitializationFree(CSharpSyntaxTree.ParseText(source).GetRoot()).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Initializer guard: aliases in another module file are refused")]
    [InlineData("using Init = System.Runtime.CompilerServices.ModuleInitializerAttribute;")]
    [InlineData("global using Init = System.Runtime.CompilerServices.ModuleInitializerAttribute;")]
    [InlineData("using InitAttribute = System.Runtime.CompilerServices.ModuleInitializerAttribute;")]
    [InlineData("global using InitAttribute = System.Runtime.CompilerServices.ModuleInitializerAttribute;")]
    public void IsInitializationFree_CrossFileAlias_ShouldRefuse(string alias)
    {
        // Arrange: aliases belong to the module, including Attribute-suffix lookup across files.
        var sources = new[] { alias, "internal static class Helper { [Init] internal static void Run() { } }" }
            .Select(source => CSharpSyntaxTree.ParseText(source).GetRoot()).ToArray();

        // Act / Assert
        IsInitializationFree(sources).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Initializer guard: immutable field and property initialization stays allowed outside schema modules")]
    [InlineData("internal static class Helper { private static readonly int[] _values = [1, 2]; }")]
    [InlineData("internal static class Helper { internal static string Value { get; } = string.Empty; }")]
    public void IsInitializationFree_StaticDataInitializer_ShouldAllow(string source)
    {
        // Arrange / Act / Assert
        IsInitializationFree(CSharpSyntaxTree.ParseText(source).GetRoot()).ShouldBeTrue();
        IsLinkSafe(source).ShouldBeFalse();
    }

    private static bool IsInitializationFree(params SyntaxNode[] sources)
    {
        var initializers = sources.SelectMany(syntax => syntax.DescendantNodes().OfType<UsingDirectiveSyntax>())
            .Where(item => item.Alias is not null && item.Name?.DescendantTokens().Any(token =>
                token.ValueText is "ModuleInitializer" or "ModuleInitializerAttribute") == true)
            .Select(item => item.Alias!.Name.Identifier.ValueText)
            .SelectMany(name => name.EndsWith("Attribute", StringComparison.Ordinal) ? new[] { name, name[..^9] } : new[] { name })
            .Append("ModuleInitializer").Append("ModuleInitializerAttribute").ToHashSet(StringComparer.Ordinal);
        return sources.All(syntax => !syntax.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Any(constructor => constructor.Modifiers.Any(SyntaxKind.StaticKeyword)) &&
            !syntax.DescendantNodes().OfType<AttributeSyntax>().Any(attribute =>
                attribute.Name.DescendantTokens().Any(token => initializers.Contains(token.ValueText))));
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Migration resources: binding and task parse keys have identical engine and SDK text")]
    public void ErrorMessages_MigrationBindings_ShouldMatchEngineResources()
    {
        // Arrange
        string root = RepositoryRoot();
        string engine = Path.Combine(root, "resources/Database/Assimalign.Cohesion.Database.Sql/src");
        string sdk = Path.Combine(root, "sdks/Assimalign.Cohesion.Sdk.Database/Tasks/src");
        var expected = XDocument.Load(Path.Combine(engine, "Properties/ErrorMessages.resx")).Descendants("data")
            .ToDictionary(item => (string)item.Attribute("name")!, item => item.Element("value")!.Value);
        var actual = XDocument.Load(Path.Combine(sdk, "Properties/ErrorMessages.resx")).Descendants("data")
            .ToDictionary(item => (string)item.Attribute("name")!, item => item.Element("value")!.Value);

        // Act: inspect both consumers so a new resource reference cannot escape the contract.
        var bindingKeys = new[] { engine, sdk }.SelectMany(directory => new[] { "SqlMigrationLoweringErrorMessages", "SqlMigrationDefaultBindings" }
            .SelectMany(binding => Regex.Matches(File.ReadAllText(Path.Combine(directory, "Internal", binding + ".cs")),
                @"ModelErrorMessages\.(\w+)").Select(match => match.Groups[1].Value)));
        var parse = Methods(Path.Combine(sdk, "Tasks/DatabaseTask.CreateMigration.cs"))
            .Single(method => method.Identifier.ValueText == "ParseMigrationStatement");
        var parseKeys = Regex.Matches(parse.Body!.ToString(), @"ErrorMessages\.(\w+)").Select(match => match.Groups[1].Value).ToArray();
        var keys = bindingKeys.Concat(parseKeys).Distinct().ToArray();

        // Assert
        parseKeys.ShouldNotBeEmpty();
        keys.ShouldNotBeEmpty();
        foreach (string key in keys)
        {
            actual.ShouldContainKey(key);
            actual[key].ShouldBe(expected[key], key);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Persisted walk: twelve traversal bodies and full parameter lists match the engine")]
    public void PersistedWalk_TwelveTraversalBodies_ShouldMatchEngineStructure()
    {
        // Arrange: compare syntax, including fallback arms, rather than sampling parsed queries.
        string root = RepositoryRoot();
        string sql = Path.Combine(root, "resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal");
        var shared = Methods(Path.Combine(root, "resources/Database/Assimalign.Cohesion.Database.Sql.Schema/shared/MigrationLowering/SqlMigrationDefaultLiteral.cs"));
        var original = Methods(Path.Combine(sql, "Binding/SqlViewQuery.cs"))
            .Concat(Methods(Path.Combine(sql, "Planning/SqlPlanner.cs"))).ToArray();
        // Act: select the twelve traversal helpers below the parser.
        var walk = shared.Where(method => method.Identifier.ValueText is not "CanonicalDefault" and not "TryParse").ToArray();
        var pairs = walk.Select(method => (Shared: method, Engine: original.Single(candidate =>
            candidate.Identifier.ValueText == method.Identifier.ValueText &&
            candidate.ParameterList.Parameters.Select(parameter => parameter.Type!.ToString()).SequenceEqual(
                method.ParameterList.Parameters.Select(parameter => parameter.Type!.ToString()))))).ToArray();

        // Assert: all twelve bodies pin default arms and recursion; parameters include default values.
        walk.Length.ShouldBe(12);
        foreach (var pair in pairs)
        {
            string.Concat(pair.Shared.ParameterList.DescendantTokens().Select(token => token.Text)).ShouldBe(
                string.Concat(pair.Engine.ParameterList.DescendantTokens().Select(token => token.Text)), pair.Shared.Identifier.ValueText);
            Structure(pair.Shared).ShouldBe(Structure(pair.Engine), pair.Shared.Identifier.ValueText);
        }
    }

    private static MethodDeclarationSyntax[] Methods(string file)
        => CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Domain names: shared admission matches engine rules and reserved-name lists")]
    public void DomainAdmission_SharedRules_ShouldMatchEngineStructure()
    {
        // Arrange: expand the engine's name lists so adding a reserved/pseudo name cannot drift.
        string root = RepositoryRoot();
        string original = Path.Combine(root, "resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/Types/SqlTypeRules.cs");
        string shared = Path.Combine(root, "resources/Database/Assimalign.Cohesion.Database.Sql.Schema/shared/MigrationLowering/SqlMigrationLowering.cs");
        var lists = CSharpSyntaxTree.ParseText(File.ReadAllText(original)).GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText is "_reservedWords" or "_pseudoNames")
            .ToDictionary(variable => variable.Identifier.ValueText, variable =>
                "new[]{" + string.Concat(variable.Initializer!.Value.DescendantTokens().Select(token => token.Text))[1..^1] + "}");

        // Act / Assert
        lists.Count.ShouldBe(2);
        foreach (string name in new[] { "ValidateDomainName", "ValidateSqlDomainName" })
        {
            string expected = Structure(Methods(original).Single(method => method.Identifier.ValueText == name));
            foreach (var list in lists) { expected = expected.Replace(list.Key, list.Value, StringComparison.Ordinal); }
            Structure(Methods(shared).Single(method => method.Identifier.ValueText == name)).ShouldBe(expected, name);
        }
    }

    private static string Structure(MethodDeclarationSyntax method)
        => string.Concat((method.Body as SyntaxNode ?? method.ExpressionBody!).DescendantTokens().Select(token => token.Text))
            .Replace("SqlPlanner.Children(", "Children(", StringComparison.Ordinal);

    private static string RepositoryRoot([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../.."));
}
