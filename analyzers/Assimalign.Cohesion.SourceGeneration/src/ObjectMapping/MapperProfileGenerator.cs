using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.SourceGeneration;

/// <summary>
/// Emits AOT-safe mapping configuration for the object mapper. Two entry points are covered:
/// <list type="bullet">
/// <item>Class-based <c>partial</c> <c>MapperProfile&lt;TTarget, TSource&gt;</c> definitions: a
/// <c>TryConfigureGenerated</c> override is added to the type.</item>
/// <item>Inline <c>MapperBuilder.AddProfile&lt;TTarget, TSource&gt;(d =&gt; …)</c> call sites: a C#
/// interceptor redirects the call to register a generated delegate-based profile, so the user
/// lambda (and its <c>Expression.Compile()</c>) never runs.</item>
/// </list>
/// In both cases the recorded mappings are replayed through the delegate-based descriptor overloads,
/// so no expression compilation happens at run time. Anything the generator cannot model falls back
/// to the reflection path.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MapperProfileGenerator : IIncrementalGenerator
{
    private const string ProfileMetadataName = "MapperProfile`2";
    private const string ProfileNamespace = "Assimalign.Cohesion.ObjectMapping";
    private const string GeneratedNamespace = "Assimalign.Cohesion.ObjectMapping.Generated";
    private const string SuppressionJustification =
        "The reflection-based mapping configuration is not executed for source-generated profiles; generated delegate mappings are used instead.";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Class-based profiles.
        var classProfiles = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsClassCandidate(node),
                transform: static (ctx, ct) => TransformClass(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!.Value);

        context.RegisterSourceOutput(classProfiles, static (spc, model) => EmitClass(spc, model));

        // Inline AddProfile<T,S>(lambda) call sites (intercepted).
        var inlineProfiles = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsInlineCandidate(node),
                transform: static (ctx, ct) => TransformInline(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!.Value)
            .Collect();

        context.RegisterImplementationSourceOutput(inlineProfiles, static (spc, models) => EmitInline(spc, models));
    }

    // ---------------------------------------------------------------------
    // Shared analysis
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds the delegate-based mapping body for a fluent chain of MapMember / MapMemberTypes /
    /// MapMemberEnumerables calls, or returns <see langword="false"/> if the chain is not fully
    /// modelable (forcing the reflection fallback).
    /// </summary>
    private static bool TryBuildBody(ExpressionSyntax chain, SemanticModel model, CancellationToken ct, out string body)
    {
        body = string.Empty;

        var mappings = new List<(string Call, int Order)>();

        foreach (var invocation in chain.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return false;
            }

            var methodName = memberAccess.Name.Identifier.Text;

            if (methodName is not ("MapMember" or "MapMemberTypes" or "MapMemberEnumerables"))
            {
                return false;
            }

            var arguments = invocation.ArgumentList.Arguments;

            if (arguments.Count != 2)
            {
                return false;
            }

            // arg0 selects a target member path: p => p.Member or p => p.A.B.C (a trailing `!` is
            // unwrapped at each level). Intermediates are created on demand for MapMember.
            if (arguments[0].Expression is not SimpleLambdaExpressionSyntax targetLambda
                || !TryParseTargetPath(targetLambda, model, ct, out var pathNames, out var intermediateNews, out var leafAccess))
            {
                return false;
            }

            // arg1 must be a SINGLE-parameter lambda (the source value), emitted verbatim as the
            // getter. A two-parameter lambda means this is the delegate-based overload
            // (getter, setter) — already AOT-safe — which must be left untouched.
            if (arguments[1].Expression is not SimpleLambdaExpressionSyntax
                && arguments[1].Expression is not ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 })
            {
                return false;
            }

            var leafName = pathNames[pathNames.Count - 1];
            var sourceText = arguments[1].Expression.ToString();
            var targetText = arguments[0].Expression.ToString();
            var order = memberAccess.Name.SpanStart;

            string call;

            switch (methodName)
            {
                case "MapMember":
                    // Only emit when the source value is assignable to the target leaf member without
                    // an implicit numeric/nullable conversion (matching the runtime IsAssignableTo check),
                    // so the generated assignment always compiles and behaves identically.
                    if (!IsScalarAssignable(model, arguments[1].Expression, leafAccess))
                    {
                        return false;
                    }

                    // Build the (possibly nested) assignment target, creating null intermediates:
                    // (target.A ??= new A()).B = value
                    var setTarget = "target";
                    for (int i = 0; i < pathNames.Count - 1; i++)
                    {
                        setTarget = $"({setTarget}.{pathNames[i]} ??= {intermediateNews[i]})";
                    }

                    call = $".MapMember({sourceText}, static (target, value) => {setTarget}.{leafName} = value)";
                    break;

                case "MapMemberTypes":
                    // Nested-object mappings target a single member.
                    if (pathNames.Count != 1)
                    {
                        return false;
                    }

                    call = $".MapMemberTypes({sourceText}, {targetText}, static (target, value) => target.{leafName} = value)";
                    break;

                default: // MapMemberEnumerables
                    // Enumerable mappings target a single member.
                    if (pathNames.Count != 1)
                    {
                        return false;
                    }

                    var collectionType = model.GetTypeInfo(leafAccess, ct).Type;
                    var conversion = GetEnumerableConversion(collectionType);
                    call = $".MapMemberEnumerables({sourceText}, {targetText}, static (target, items) => target.{leafName} = {conversion})";
                    break;
            }

            mappings.Add((call, order));
        }

        if (mappings.Count == 0)
        {
            return false;
        }

        mappings.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        var builder = new StringBuilder();
        builder.Append("        descriptor");

        for (int i = 0; i < mappings.Count; i++)
        {
            builder.AppendLine();
            builder.Append("            ").Append(mappings[i].Call);

            if (i == mappings.Count - 1)
            {
                builder.Append(';');
            }
        }

        body = builder.ToString();
        return true;
    }

    private static bool IsScalarAssignable(SemanticModel model, ExpressionSyntax sourceLambda, MemberAccessExpressionSyntax targetMember)
    {
        ExpressionSyntax? sourceBody = sourceLambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body as ExpressionSyntax,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body as ExpressionSyntax,
            _ => null
        };

        if (sourceBody is null)
        {
            return false;
        }

        var sourceType = model.GetTypeInfo(sourceBody).Type;
        var targetType = model.GetTypeInfo(targetMember).Type;

        if (sourceType is null || targetType is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(sourceType, targetType))
        {
            return true;
        }

        var conversion = model.Compilation.ClassifyConversion(sourceType, targetType);

        return conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing;
    }

    /// <summary>
    /// Parses a target member-access lambda (<c>p =&gt; p.A.B</c>, trailing <c>!</c> unwrapped at each
    /// level) into its root-to-leaf member names. For each intermediate member it records a
    /// <c>new T()</c> expression and verifies the member is a reference type with a public
    /// parameterless constructor (so it can be created on demand). Returns <see langword="false"/>
    /// when the body is not a pure member-access chain rooted at the parameter, or an intermediate
    /// cannot be created.
    /// </summary>
    private static bool TryParseTargetPath(
        SimpleLambdaExpressionSyntax targetLambda,
        SemanticModel model,
        CancellationToken ct,
        out List<string> names,
        out List<string> intermediateNews,
        out MemberAccessExpressionSyntax leafAccess)
    {
        names = new List<string>();
        intermediateNews = new List<string>();
        leafAccess = null!;

        var parameterName = targetLambda.Parameter.Identifier.Text;
        var accesses = new List<MemberAccessExpressionSyntax>();
        var current = Unwrap(targetLambda.Body as ExpressionSyntax);

        while (current is MemberAccessExpressionSyntax access)
        {
            accesses.Add(access);
            current = Unwrap(access.Expression);
        }

        if (accesses.Count == 0
            || current is not IdentifierNameSyntax identifier
            || identifier.Identifier.Text != parameterName)
        {
            return false;
        }

        leafAccess = accesses[0]; // leaf-first, before reversing
        accesses.Reverse();       // root-to-leaf

        for (int i = 0; i < accesses.Count; i++)
        {
            names.Add(accesses[i].Name.Identifier.Text);

            if (i < accesses.Count - 1)
            {
                if (model.GetTypeInfo(accesses[i], ct).Type is not INamedTypeSymbol type
                    || !type.IsReferenceType
                    || !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                {
                    return false;
                }

                intermediateNews.Add($"new {type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}()");
            }
        }

        return true;

        static ExpressionSyntax? Unwrap(ExpressionSyntax? expression)
        {
            while (expression is PostfixUnaryExpressionSyntax postfix
                && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                expression = postfix.Operand;
            }

            return expression;
        }
    }

    private static string GetEnumerableConversion(ITypeSymbol? memberType)
    {
        if (memberType is IArrayTypeSymbol)
        {
            return "global::System.Linq.Enumerable.ToArray(items)";
        }

        if (memberType is INamedTypeSymbol named && named.TypeArguments.Length == 1)
        {
            var element = named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            switch (named.MetadataName)
            {
                case "HashSet`1":
                case "ISet`1":
                case "IReadOnlySet`1":
                    return $"new global::System.Collections.Generic.HashSet<{element}>(items)";
                case "Queue`1":
                    return $"new global::System.Collections.Generic.Queue<{element}>(items)";
                case "Stack`1":
                    return $"new global::System.Collections.Generic.Stack<{element}>(items)";
            }
        }

        // List<T>, IList<T>, ICollection<T>, IEnumerable<T>, IReadOnlyList<T>, IReadOnlyCollection<T>, etc.
        return "global::System.Linq.Enumerable.ToList(items)";
    }

    private static bool IsDocumentableMember(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.MethodKind is MethodKind.Ordinary
                or MethodKind.Constructor
                or MethodKind.StaticConstructor
                or MethodKind.Destructor
                or MethodKind.UserDefinedOperator
                or MethodKind.Conversion,
            IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol => true,
            _ => false
        };
    }

    // ---------------------------------------------------------------------
    // Class-based profiles
    // ---------------------------------------------------------------------

    private static bool IsClassCandidate(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax c
            && c.BaseList is not null
            && c.Modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    private static ClassModel? TransformClass(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDeclaration = (ClassDeclarationSyntax)ctx.Node;

        if (ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) is not INamedTypeSymbol classSymbol)
        {
            return null;
        }

        if (!classSymbol.TypeParameters.IsEmpty || classSymbol.ContainingType is not null)
        {
            return null;
        }

        var baseType = classSymbol.BaseType;

        if (baseType is null
            || baseType.MetadataName != ProfileMetadataName
            || baseType.ContainingNamespace?.ToDisplayString() != ProfileNamespace
            || baseType.TypeArguments.Length != 2)
        {
            return null;
        }

        var configure = classDeclaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Configure"
                && m.ParameterList.Parameters.Count == 1
                && m.Modifiers.Any(static mod => mod.IsKind(SyntaxKind.OverrideKeyword)));

        if (configure is null)
        {
            return null;
        }

        ExpressionSyntax? chain = configure.ExpressionBody?.Expression;

        if (chain is null
            && configure.Body is { Statements.Count: 1 } body
            && body.Statements[0] is ExpressionStatementSyntax statement)
        {
            chain = statement.Expression;
        }

        if (chain is null || !TryBuildBody(chain, ctx.SemanticModel, ct, out var mappingBody))
        {
            return null;
        }

        var targetType = baseType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var sourceType = baseType.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : classSymbol.ContainingNamespace.ToDisplayString();

        return new ClassModel(namespaceName, classSymbol.Name, targetType, sourceType, mappingBody);
    }

    private static void EmitClass(SourceProductionContext spc, ClassModel model)
    {
        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();

        var indent = string.Empty;

        if (model.Namespace is not null)
        {
            builder.Append("namespace ").AppendLine(model.Namespace);
            builder.AppendLine("{");
            indent = "    ";
        }

        builder.Append(indent)
            .Append("[global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"")
            .Append(SuppressionJustification).AppendLine("\")]");
        builder.Append(indent)
            .Append("[global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"AOT\", \"IL3050\", Justification = \"")
            .Append(SuppressionJustification).AppendLine("\")]");
        builder.Append(indent).Append("partial class ").AppendLine(model.ClassName);
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    /// <summary>Source-generated, AOT-safe mapping configuration.</summary>");
        builder.Append(indent)
            .Append("    protected override bool TryConfigureGenerated(global::Assimalign.Cohesion.ObjectMapping.MapperProfileDescriptor<")
            .Append(model.TargetType).Append(", ").Append(model.SourceType).AppendLine("> descriptor)");
        builder.Append(indent).AppendLine("    {");

        foreach (var line in model.Body.Split('\n'))
        {
            builder.Append(indent).AppendLine(line.TrimEnd('\r'));
        }

        builder.Append(indent).AppendLine("        return true;");
        builder.Append(indent).AppendLine("    }");
        builder.Append(indent).AppendLine("}");

        if (model.Namespace is not null)
        {
            builder.AppendLine("}");
        }

        spc.AddSource($"{model.ClassName}.MapperProfile.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    // ---------------------------------------------------------------------
    // Inline AddProfile<T,S>(lambda) call sites (intercepted)
    // ---------------------------------------------------------------------

    private static bool IsInlineCandidate(SyntaxNode node)
    {
        return node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "AddProfile", TypeArgumentList.Arguments.Count: 2 } },
            ArgumentList.Arguments.Count: 1
        };
    }

    private static InlineModel? TransformInline(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        // Must be MapperBuilder.AddProfile<TTarget, TSource>(Action<MapperProfileDescriptor<TTarget, TSource>>).
        if (method.Name != "AddProfile"
            || method.TypeArguments.Length != 2
            || method.ContainingType?.Name != "MapperBuilder"
            || method.ContainingType.ContainingNamespace?.ToDisplayString() != ProfileNamespace)
        {
            return null;
        }

        // The single argument must be a lambda whose body is the fluent configuration chain.
        var argument = invocation.ArgumentList.Arguments[0].Expression;

        CSharpSyntaxNode? lambdaBody = argument switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
            _ => null
        };

        // The body is either an expression (d => d.MapMember(...)) or a single-statement block
        // (d => { d.MapMember(...); }).
        ExpressionSyntax? chain = lambdaBody switch
        {
            ExpressionSyntax expression => expression,
            BlockSyntax { Statements.Count: 1 } block when block.Statements[0] is ExpressionStatementSyntax statement => statement.Expression,
            _ => null
        };

        if (chain is null || !TryBuildBody(chain, ctx.SemanticModel, ct, out var mappingBody))
        {
            return null;
        }

        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);

        if (location is null)
        {
            return null;
        }

        var targetType = method.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var sourceType = method.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Suppress the (unreachable) expression-based configuration for the member that contains the
        // inline call. Walk past lambdas / local functions to the first real member or type.
        var enclosing = ctx.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, ct);

        while (enclosing is not null && !IsDocumentableMember(enclosing))
        {
            enclosing = enclosing.ContainingSymbol;
        }

        // A referenceable member (a normal method/property/...) gets a precise member-scoped
        // suppression. A synthesized member — e.g. a top-level program's '<Main>$', whose
        // documentation id the analyzer will not match — falls back to a type-scoped suppression.
        string? suppressionTarget;
        string suppressionScope;

        if (enclosing is not null and not INamedTypeSymbol
            && enclosing.CanBeReferencedByName
            && enclosing.GetDocumentationCommentId() is { } memberDocId)
        {
            suppressionTarget = memberDocId;
            suppressionScope = "member";
        }
        else
        {
            var containingType = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
            suppressionTarget = containingType?.GetDocumentationCommentId();
            suppressionScope = "type";
        }

        return new InlineModel(
            targetType,
            sourceType,
            mappingBody,
            location.GetInterceptsLocationAttributeSyntax(),
            suppressionTarget,
            suppressionScope);
    }

    private static void EmitInline(SourceProductionContext spc, ImmutableArray<InlineModel> models)
    {
        if (models.IsDefaultOrEmpty)
        {
            return;
        }

        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();

        // Suppress the (unreachable) expression-based configuration for each distinct target/scope.
        foreach (var suppression in models
            .Where(static m => m.SuppressionTargetDocId is not null)
            .Select(static m => (Target: m.SuppressionTargetDocId, m.SuppressionScope))
            .Distinct())
        {
            builder.Append("[assembly: global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Scope = \"")
                .Append(suppression.SuppressionScope).Append("\", Target = \"").Append(suppression.Target)
                .Append("\", Justification = \"").Append(SuppressionJustification).AppendLine("\")]");
            builder.Append("[assembly: global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"AOT\", \"IL3050\", Scope = \"")
                .Append(suppression.SuppressionScope).Append("\", Target = \"").Append(suppression.Target)
                .Append("\", Justification = \"").Append(SuppressionJustification).AppendLine("\")]");
        }

        builder.AppendLine();

        // The interceptors reference an InterceptsLocationAttribute. A file-scoped definition avoids
        // collisions with other generators/assemblies that emit their own.
        builder.AppendLine("namespace System.Runtime.CompilerServices");
        builder.AppendLine("{");
        builder.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        builder.AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute");
        builder.AppendLine("    {");
        builder.AppendLine("        public InterceptsLocationAttribute(int version, string data) { _ = version; _ = data; }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();

        builder.Append("namespace ").AppendLine(GeneratedNamespace);
        builder.AppendLine("{");
        builder.AppendLine("    file static class MapperInlineInterceptors");
        builder.AppendLine("    {");

        for (int i = 0; i < models.Length; i++)
        {
            builder.Append("        ").AppendLine(models[i].InterceptsLocationAttribute);
            builder.Append("        public static global::Assimalign.Cohesion.ObjectMapping.MapperBuilder Intercept_")
                .Append(i)
                .Append("<TTarget, TSource>(this global::Assimalign.Cohesion.ObjectMapping.MapperBuilder builder, global::System.Action<global::Assimalign.Cohesion.ObjectMapping.MapperProfileDescriptor<TTarget, TSource>> configure)");
            builder.AppendLine();
            builder.Append("            => builder.AddProfile(new __InlineMapperProfile_").Append(i).AppendLine("());");

            if (i < models.Length - 1)
            {
                builder.AppendLine();
            }
        }

        builder.AppendLine("    }");

        for (int i = 0; i < models.Length; i++)
        {
            var model = models[i];

            builder.AppendLine();
            builder.Append("    file sealed class __InlineMapperProfile_").Append(i)
                .Append(" : global::Assimalign.Cohesion.ObjectMapping.MapperProfile<")
                .Append(model.TargetType).Append(", ").Append(model.SourceType).AppendLine(">");
            builder.AppendLine("    {");
            builder.Append("        protected override void Configure(global::Assimalign.Cohesion.ObjectMapping.MapperProfileDescriptor<")
                .Append(model.TargetType).Append(", ").Append(model.SourceType).AppendLine("> descriptor)");
            builder.AppendLine("        {");

            foreach (var line in model.Body.Split('\n'))
            {
                builder.Append("    ").AppendLine(line.TrimEnd('\r'));
            }

            builder.AppendLine("        }");
            builder.AppendLine("    }");
        }

        builder.AppendLine("}");

        spc.AddSource("MapperInlineInterceptors.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private readonly record struct ClassModel(
        string? Namespace,
        string ClassName,
        string TargetType,
        string SourceType,
        string Body);

    private readonly record struct InlineModel(
        string TargetType,
        string SourceType,
        string Body,
        string InterceptsLocationAttribute,
        string? SuppressionTargetDocId,
        string SuppressionScope);
}
