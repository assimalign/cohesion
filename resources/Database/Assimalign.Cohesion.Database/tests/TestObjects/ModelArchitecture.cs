using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;

// Deviates from the repo namespace-matches-assembly rule: Database-owned test support is linked into each model's own test assembly; no shipped assembly shares these types.
namespace Assimalign.Cohesion.Database.Tests.TestObjects;

internal static class ModelArchitecture
{
    internal static void Verify()
    {
        // Arrange: these are the actual source, generated source and reference inputs used by Csc.
        string directory = Path.Combine(AppContext.BaseDirectory, "ModelArchitecture");
        string[] settings = File.ReadAllLines(Path.Combine(directory, "DatabaseArchitecture.settings.txt"));
        var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: settings[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
        SyntaxTree[] trees = File.ReadAllLines(Path.Combine(directory, "DatabaseArchitecture.sources.txt"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file))
            .ToArray();
        MetadataReference[] references = File.ReadAllLines(Path.Combine(directory, "DatabaseArchitecture.references.txt"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file => MetadataReference.CreateFromFile(file)).ToArray();
        var compilation = CSharpCompilation.Create(settings[0], trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        // Act: resolve symbols rather than matching namespace or identifier spelling.
        Diagnostic[] errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        string[] violations = FindViolations(compilation);

        // Assert: a broken reconstruction cannot make the dependency check pass vacuously.
        errors.ShouldBeEmpty(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        if (!settings[0].EndsWith(".Cache", StringComparison.Ordinal))
        {
            trees.ShouldContain(tree => GetLayer(tree.FilePath) == Layer.Catalog);
            trees.ShouldContain(tree => GetLayer(tree.FilePath) == Layer.Storage);
        }
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
        VerifyDetection(references);
    }

    private static string[] FindViolations(CSharpCompilation compilation)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            Layer from = GetLayer(tree.FilePath);
            if (from is not (Layer.Storage or Layer.Catalog)) { continue; }
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodesAndSelf())
            {
                if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax) { Check(model.GetDeclaredSymbol(node)); }
                SymbolInfo symbol = model.GetSymbolInfo(node);
                Check(symbol.Symbol);
                foreach (ISymbol candidate in symbol.CandidateSymbols) { Check(candidate); }
                if (node is ExpressionSyntax or AttributeSyntax or ConstructorInitializerSyntax)
                {
                    TypeInfo type = model.GetTypeInfo(node);
                    Check(type.Type);
                    Check(type.ConvertedType);
                }

                void Check(ISymbol? target)
                {
                    if (target is IAliasSymbol alias) { target = alias.Target; }
                    if (target is null) { return; }
                    INamedTypeSymbol? owner = target as INamedTypeSymbol ?? target.ContainingType;
                    if (owner is null) { return; }
                    SyntaxReference[] declarations = owner.OriginalDefinition.DeclaringSyntaxReferences.ToArray();
                    if (declarations.Any(declaration => !declaration.SyntaxTree.FilePath.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal)))
                    {
                        declarations = declarations.Where(declaration => !declaration.SyntaxTree.FilePath.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal)).ToArray();
                    }
                    foreach (SyntaxReference declaration in declarations)
                    {
                        Layer to = GetLayer(declaration.SyntaxTree.FilePath);
                        if (to == Layer.Engine || (from == Layer.Storage && to == Layer.Catalog))
                        {
                            FileLinePositionSpan location = node.GetLocation().GetLineSpan();
                            violations.Add($"{location.Path}:{location.StartLinePosition.Line + 1}: {from} references {to} type {owner.ToDisplayString()}");
                        }
                    }
                    if (owner.ContainingType is not null) { Check(owner.ContainingType); }
                    foreach (ITypeSymbol argument in owner.TypeArguments)
                    {
                        if (!SymbolEqualityComparer.Default.Equals(argument, owner)) { CheckType(argument); }
                    }
                }

                void CheckType(ITypeSymbol type)
                {
                    if (type is IArrayTypeSymbol array) { CheckType(array.ElementType); }
                    else if (type is IPointerTypeSymbol pointer) { CheckType(pointer.PointedAtType); }
                    else if (type is INamedTypeSymbol named) { Check(named); }
                }
            }
        }
        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static Layer GetLayer(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Contains("/src/Internal/Storage/", StringComparison.Ordinal) || normalized.Contains("/src/Storage/", StringComparison.Ordinal)) { return Layer.Storage; }
        if (normalized.Contains("/src/Internal/Catalog/", StringComparison.Ordinal) || normalized.Contains("/src/Catalog/", StringComparison.Ordinal)) { return Layer.Catalog; }
        // Resources, assembly attributes and their generated accessors are shared infrastructure.
        if (normalized.Contains("/Properties/", StringComparison.Ordinal) || normalized.EndsWith("/ErrorMessages.g.cs", StringComparison.Ordinal) || normalized.EndsWith("/Resources.g.cs", StringComparison.Ordinal)
            || normalized.EndsWith(".AssemblyInfo.cs", StringComparison.Ordinal) || normalized.EndsWith(".AssemblyAttributes.cs", StringComparison.Ordinal)) { return Layer.Infrastructure; }
        return Layer.Engine;
    }

    private static void VerifyDetection(MetadataReference[] references)
    {
        // Prove that aliases, inherited/generic types and member calls cannot hide a forbidden edge.
        SyntaxTree[] probes =
        [
            CSharpSyntaxTree.ParseText("namespace Probe; public class Engine { public static void Run() {} }", path: "/src/Engine.cs"),
            CSharpSyntaxTree.ParseText("namespace Probe; public class Catalog { } public class BadCatalog : Engine { }", path: "/src/Internal/Catalog/Catalog.cs"),
            CSharpSyntaxTree.ParseText("using Alias = Probe.Catalog; namespace Probe; public class Store { public Alias? Value; public System.Collections.Generic.List<Engine>? Engines; public void Run() { Engine.Run(); } }", path: "/src/Storage/Store.cs")
        ];
        var probe = CSharpCompilation.Create("ArchitectureProbe", probes, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        probe.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        string[] violations = FindViolations(probe);
        violations.ShouldContain(value => value.Contains("Catalog references Engine", StringComparison.Ordinal));
        violations.ShouldContain(value => value.Contains("Storage references Catalog", StringComparison.Ordinal));
        violations.ShouldContain(value => value.Contains("Storage references Engine", StringComparison.Ordinal));
    }

    private enum Layer { Infrastructure, Storage, Catalog, Engine }
}
