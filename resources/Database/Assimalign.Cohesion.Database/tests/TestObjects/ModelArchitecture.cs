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
    // Seeded from the Storage/Catalog csprojs at a64426436, including their kernel closure.
    // Tightened to current symbol usages. Engine code has no outside-assembly limit.
    private static readonly HashSet<string> _storageAssemblies = new(StringComparer.Ordinal)
    {
        "Assimalign.Cohesion.Database.Storage",
        "Assimalign.Cohesion.Database.Transactions",
        "Assimalign.Cohesion.Database.Indexing",
    };

    private static readonly HashSet<string> _catalogAssemblies = new(_storageAssemblies, StringComparer.Ordinal)
    {
        "Assimalign.Cohesion.Database",
        "Assimalign.Cohesion.Database.Types",
    };

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
        // Trust the actual framework reference pack, never a System.* assembly-name wildcard.
        var frameworkAssemblies = references.OfType<PortableExecutableReference>()
            .Where(reference => reference.FilePath!.Replace('\\', '/').Contains("/packs/Microsoft.NETCore.App.Ref/", StringComparison.OrdinalIgnoreCase))
            .Select(reference => Path.GetFileNameWithoutExtension(reference.FilePath!)).ToHashSet(StringComparer.Ordinal);
        frameworkAssemblies.ShouldContain("System.Runtime");
        var compilation = CSharpCompilation.Create(settings[0], trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        // Act: resolve symbols rather than matching namespace or identifier spelling.
        Diagnostic[] errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        var outsideUsages = new HashSet<string>(StringComparer.Ordinal);
        string[] violations = FindViolations(compilation, frameworkAssemblies, outsideUsages);
        Console.WriteLine(string.Join(Environment.NewLine, outsideUsages.Order(StringComparer.Ordinal)));

        // Assert: a broken reconstruction cannot make the dependency check pass vacuously.
        errors.ShouldBeEmpty(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        trees.ShouldContain(tree => GetLayer(tree.FilePath) == Layer.Catalog);
        trees.ShouldContain(tree => GetLayer(tree.FilePath) == Layer.Storage);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
        VerifyDetection(references, frameworkAssemblies);
    }

    private static string[] FindViolations(CSharpCompilation compilation, HashSet<string> frameworkAssemblies, HashSet<string>? outsideUsages = null)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            Layer from = GetLayer(tree.FilePath);
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodesAndSelf())
            {
                if (node is BaseTypeDeclarationSyntax && model.GetDeclaredSymbol(node) is INamedTypeSymbol declared)
                {
                    if (declared.Name.EndsWith("StorageStrategy", StringComparison.Ordinal)
                        && !tree.FilePath.Replace('\\', '/').Contains("/src/Internal/Engine/Storage/", StringComparison.Ordinal))
                    {
                        Report($"storage strategy {declared.Name} must live in Internal/Engine/Storage");
                    }
                    for (INamedTypeSymbol? parent = declared.BaseType; parent is not null; parent = parent.BaseType)
                    {
                        if (parent.ToDisplayString() == "Assimalign.Cohesion.Database.Transactions.TransactionRecordSpace" && from != Layer.Storage)
                        {
                            Report($"transaction record space {declared.Name} must live in Storage");
                        }
                    }
                }
                if (from is not (Layer.Storage or Layer.Catalog)) { continue; }
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
                    IAssemblySymbol assembly = owner.ContainingAssembly;
                    if (!SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly) && !frameworkAssemblies.Contains(assembly.Identity.Name))
                    {
                        outsideUsages?.Add($"{from}: {assembly.Identity.Name}");
                        if (!(from == Layer.Storage ? _storageAssemblies : _catalogAssemblies).Contains(assembly.Identity.Name)
                            // Graph.Storage already reached Types through its old Indexing reference.
                            && !(from == Layer.Storage && compilation.AssemblyName == "Assimalign.Cohesion.Database.Graph"
                                && assembly.Identity.Name == "Assimalign.Cohesion.Database.Types"))
                        {
                            Report($"{from} references disallowed assembly {assembly.Identity.Name} through type {owner.ToDisplayString()}");
                        }
                    }
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
                            Report($"{from} references {to} type {owner.ToDisplayString()}");
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

                void Report(string message)
                {
                    FileLinePositionSpan location = node.GetLocation().GetLineSpan();
                    violations.Add($"{location.Path}:{location.StartLinePosition.Line + 1}: {message}");
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

    private static void VerifyDetection(MetadataReference[] references, HashSet<string> frameworkAssemblies)
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
        string[] violations = FindViolations(probe, frameworkAssemblies);
        violations.ShouldContain(value => value.Contains("Catalog references Engine", StringComparison.Ordinal));
        violations.ShouldContain(value => value.Contains("Storage references Catalog", StringComparison.Ordinal));
        violations.ShouldContain(value => value.Contains("Storage references Engine", StringComparison.Ordinal));

        // A matching namespace must not hide an outside assembly, including in generic arguments.
        var outside = CSharpCompilation.Create("Disallowed.Assembly",
            [CSharpSyntaxTree.ParseText("namespace System; public class Outside { public static void Run() {} }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        outside.Emit(bytes).Success.ShouldBeTrue();
        var externalProbe = CSharpCompilation.Create("ExternalArchitectureProbe",
        [
            CSharpSyntaxTree.ParseText("using Alias = System.Outside; public class Store { public System.Collections.Generic.List<Alias>? Values; public void Run() { Alias.Run(); } }", path: "/src/Storage/Store.cs"),
            CSharpSyntaxTree.ParseText("public class Catalog { public System.Outside? Value; }", path: "/src/Catalog/Catalog.cs"),
            CSharpSyntaxTree.ParseText("public class Engine { public System.Outside? Value; }", path: "/src/Engine.cs"),
        ], references.Append(MetadataReference.CreateFromImage(bytes.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        externalProbe.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        string[] externalViolations = FindViolations(externalProbe, frameworkAssemblies);
        externalViolations.ShouldContain(value => value.Contains("Storage references disallowed assembly Disallowed.Assembly", StringComparison.Ordinal));
        externalViolations.ShouldContain(value => value.Contains("Catalog references disallowed assembly Disallowed.Assembly", StringComparison.Ordinal));
        externalViolations.ShouldNotContain(value => value.Contains("/src/Engine.cs:", StringComparison.Ordinal));

        // Placement is independent of current dependencies: moving an adapter or strategy is rejected.
        var placementProbe = CSharpCompilation.Create("PlacementProbe",
        [
            CSharpSyntaxTree.ParseText("internal class ProbeStorageStrategy { }", path: "/src/Internal/ProbeStorageStrategy.cs"),
            CSharpSyntaxTree.ParseText("internal abstract class Records : Assimalign.Cohesion.Database.Transactions.TransactionRecordSpace { }", path: "/src/Internal/Engine/Storage/Records.cs"),
        ], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        placementProbe.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        string[] placementViolations = FindViolations(placementProbe, frameworkAssemblies);
        placementViolations.ShouldContain(value => value.Contains("storage strategy ProbeStorageStrategy must live in Internal/Engine/Storage", StringComparison.Ordinal));
        placementViolations.ShouldContain(value => value.Contains("transaction record space Records must live in Storage", StringComparison.Ordinal));
    }

    private enum Layer { Infrastructure, Storage, Catalog, Engine }
}
