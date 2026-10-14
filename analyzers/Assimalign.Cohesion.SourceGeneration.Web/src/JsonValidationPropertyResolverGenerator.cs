using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.SourceGeneration.Web;

/// <summary>
/// Adds reflection-free validation member lookup to source-generated JSON contexts when the
/// application references the optional Web.Validation property-resolver contract.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class JsonValidationPropertyResolverGenerator : IIncrementalGenerator
{
    private const string resolverName = "Assimalign.Cohesion.Web.Validation.IValidationJsonPropertyResolver";
    private const string contextName = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string serializableName = "System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string derivedTypeAttribute = "System.Text.Json.Serialization.JsonDerivedTypeAttribute";
    private const string propertyNameAttribute = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
    private const string ignoreAttribute = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string includeAttribute = "System.Text.Json.Serialization.JsonIncludeAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sources = context.SyntaxProvider.ForAttributeWithMetadataName(
                serializableName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntax, cancellationToken) => Generate(syntax, cancellationToken))
            .Where(static source => source is not null);

        context.RegisterSourceOutput(sources.Collect(), static (production, values) =>
        {
            // Attributes can be split across several declarations of the same partial context.
            foreach (GeneratedSource source in values.Select(value => value!).Distinct())
            {
                production.AddSource(source.HintName, SourceText.From(source.Text, Encoding.UTF8));
            }
        });
    }

    private static GeneratedSource? Generate(GeneratorAttributeSyntaxContext syntax, CancellationToken cancellationToken)
    {
        var context = (INamedTypeSymbol)syntax.TargetSymbol;
        Compilation compilation = syntax.SemanticModel.Compilation;
        INamedTypeSymbol? resolver = compilation.GetTypeByMetadataName(resolverName);

        if (resolver is null
            || !compilation.IsSymbolAccessibleWithin(resolver, context)
            || context.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, resolver))
            || !DerivesFromContext(context)
            || !CanAugment(context, cancellationToken))
        {
            return null;
        }

        var pending = new Queue<(ITypeSymbol Type, HashSet<INamedTypeSymbol> Ancestors)>();
        foreach (AttributeData attribute in context.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == serializableName
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is ITypeSymbol root)
            {
                pending.Enqueue((root, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)));
            }
        }

        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var models = new List<(string TypeName, List<(string MemberName, string? JsonName, bool RequiresIncludeFields)> Members)>();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = pending.Dequeue();
            ITypeSymbol type = candidate.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            if (!visited.Add(type))
            {
                continue;
            }

            if (type is IArrayTypeSymbol array)
            {
                pending.Enqueue((array.ElementType, candidate.Ancestors));
                continue;
            }

            if (type is not INamedTypeSymbol named || named.IsUnboundGenericType || named.IsRefLikeType)
            {
                continue;
            }

            foreach (ITypeSymbol argument in named.TypeArguments)
            {
                pending.Enqueue((argument, candidate.Ancestors));
            }

            foreach (INamedTypeSymbol implemented in named.AllInterfaces)
            {
                foreach (ITypeSymbol argument in implemented.TypeArguments)
                {
                    pending.Enqueue((argument, candidate.Ancestors));
                }
            }

            if (named.SpecialType != SpecialType.None
                || named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface)
                || named.ContainingNamespace.ToDisplayString() == "System"
                || named.ContainingNamespace.ToDisplayString().StartsWith("System.", StringComparison.Ordinal)
                || !compilation.IsSymbolAccessibleWithin(named, context))
            {
                continue;
            }

            if (named.BaseType is { } baseType)
            {
                pending.Enqueue((baseType, candidate.Ancestors));
            }

            // A model such as Node<T>.Next: Node<List<T>> must not expand forever. Record the
            // immediate contract, then stop following members when its definition recurs on this path.
            bool expandMembers = !candidate.Ancestors.Contains(named.OriginalDefinition);
            var ancestors = new HashSet<INamedTypeSymbol>(candidate.Ancestors, SymbolEqualityComparer.Default)
            {
                named.OriginalDefinition
            };

            // System.Text.Json registers attributed derived types even without an explicit root.
            // The visited set breaks base/derived cycles; preserve ancestry for member expansion.
            foreach (AttributeData attribute in named.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == derivedTypeAttribute
                    && attribute.ConstructorArguments.Length > 0
                    && attribute.ConstructorArguments[0].Value is ITypeSymbol derivedType)
                {
                    pending.Enqueue((derivedType, ancestors));
                }
            }

            var members = new List<(string MemberName, string? JsonName, bool RequiresIncludeFields)>();
            var memberNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (INamedTypeSymbol owner in MemberOwners(named))
            {
                foreach (ISymbol member in owner.GetMembers())
                {
                    ITypeSymbol? memberType = member switch
                    {
                        IPropertySymbol { IsStatic: false, IsIndexer: false } property => property.Type,
                        IFieldSymbol { IsStatic: false } field => field.Type,
                        _ => null
                    };

                    bool included = member.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == includeAttribute);
                    if (memberType is null
                        || (member.DeclaredAccessibility != Accessibility.Public && !included)
                        || !memberNames.Add(member.Name)
                        || IsAlwaysIgnored(member))
                    {
                        continue;
                    }

                    if (expandMembers)
                    {
                        pending.Enqueue((memberType, ancestors));
                    }
                    AttributeData? rename = member.GetAttributes().FirstOrDefault(attribute =>
                        attribute.AttributeClass?.ToDisplayString() == propertyNameAttribute);
                    string? jsonName = rename is { ConstructorArguments.Length: > 0 }
                        ? rename.ConstructorArguments[0].Value as string
                        : null;
                    members.Add((member.Name, jsonName, member is IFieldSymbol && !included));
                }
            }

            if (members.Count > 0)
            {
                models.Add((named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), members));
            }
        }

        var builder = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!context.ContainingNamespace.IsGlobalNamespace)
        {
            builder.Append("namespace ").Append(context.ContainingNamespace.ToDisplayString()).AppendLine(";");
        }

        var containers = new Stack<INamedTypeSymbol>();
        for (INamedTypeSymbol? owner = context; owner is not null; owner = owner.ContainingType)
        {
            containers.Push(owner);
        }

        int depth = containers.Count;
        foreach (INamedTypeSymbol owner in containers)
        {
            string kind = owner.IsRecord ? owner.TypeKind == TypeKind.Struct ? "record struct" : "record class"
                : owner.TypeKind == TypeKind.Struct ? "struct" : owner.TypeKind == TypeKind.Interface ? "interface" : "class";
            builder.Append("partial ").Append(kind).Append(' ').Append(Escape(owner.Name));
            if (owner.TypeParameters.Length > 0)
            {
                builder.Append('<').Append(string.Join(", ", owner.TypeParameters.Select(parameter => Escape(parameter.Name)))).Append('>');
            }
            if (SymbolEqualityComparer.Default.Equals(owner, context))
            {
                builder.Append(" : global::").Append(resolverName);
            }
            builder.AppendLine().AppendLine("{");
        }

        builder.Append("    global::System.Text.Json.Serialization.Metadata.JsonPropertyInfo? global::").Append(resolverName)
            .AppendLine(".GetProperty(global::System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo, string memberName)")
            .AppendLine("    {")
            .AppendLine("        string? jsonName = null;");

        foreach (var model in models.OrderBy(model => model.TypeName, StringComparer.Ordinal))
        {
            builder.Append("        if (typeInfo.Type == typeof(").Append(model.TypeName).AppendLine("))")
                .AppendLine("        {")
                .AppendLine("            jsonName = memberName switch")
                .AppendLine("            {");
            foreach (var member in model.Members.OrderBy(member => member.MemberName, StringComparer.Ordinal))
            {
                builder.Append("                ").Append(SymbolDisplay.FormatLiteral(member.MemberName, true));
                if (member.RequiresIncludeFields)
                {
                    builder.Append(" when typeInfo.Options.IncludeFields");
                }
                builder.Append(" => ");
                builder.Append(member.JsonName is null
                    ? "typeInfo.Options.PropertyNamingPolicy?.ConvertName(memberName) ?? memberName"
                    : SymbolDisplay.FormatLiteral(member.JsonName, true));
                builder.AppendLine(",");
            }
            builder.AppendLine("                _ => null")
                .AppendLine("            };")
                .AppendLine("        }");
        }

        builder.AppendLine("        if (jsonName is not null)")
            .AppendLine("        {")
            .AppendLine("            foreach (var property in typeInfo.Properties)")
            .AppendLine("            {")
            .AppendLine("                if (global::System.String.Equals(property.Name, jsonName, global::System.StringComparison.Ordinal))")
            .AppendLine("                {")
            .AppendLine("                    return property;")
            .AppendLine("                }")
            .AppendLine("            }")
            .AppendLine("        }")
            .AppendLine("        return null;")
            .AppendLine("    }");
        for (int index = 0; index < depth; index++)
        {
            builder.AppendLine("}");
        }

        string identity = context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string hint = string.Concat(identity.Select(character => char.IsLetterOrDigit(character)
            ? character.ToString()
            : "_" + ((int)character).ToString("X4", CultureInfo.InvariantCulture)));
        return new GeneratedSource(hint + ".ValidationJsonProperties.g.cs", builder.ToString());
    }

    private static bool DerivesFromContext(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? owner = type.BaseType; owner is not null; owner = owner.BaseType)
        {
            if (owner.ToDisplayString() == contextName)
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> MemberOwners(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? owner = type; owner is not null; owner = owner.BaseType)
        {
            yield return owner;
        }
        if (type.TypeKind == TypeKind.Interface)
        {
            foreach (INamedTypeSymbol owner in type.AllInterfaces)
            {
                yield return owner;
            }
        }
    }

    private static bool IsAlwaysIgnored(ISymbol member)
    {
        AttributeData? ignored = member.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == ignoreAttribute);
        if (ignored is null)
        {
            return false;
        }
        foreach (KeyValuePair<string, TypedConstant> argument in ignored.NamedArguments)
        {
            if (argument.Key == "Condition")
            {
                return argument.Value.Value is 1; // JsonIgnoreCondition.Always.
            }
        }
        return true; // The attribute's default condition is Always.
    }

    private static bool CanAugment(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        for (INamedTypeSymbol? owner = type; owner is not null; owner = owner.ContainingType)
        {
            if (owner.IsFileLocal || owner.DeclaringSyntaxReferences.Length == 0
                || owner.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration
                    || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                return false;
            }
        }
        return true;
    }

    private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    private sealed record GeneratedSource(string HintName, string Text);
}
