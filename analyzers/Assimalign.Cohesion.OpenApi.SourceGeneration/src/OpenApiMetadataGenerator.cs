using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration;

/// <summary>
/// Discovers the OpenApi authoring attributes at compile time and emits an
/// <c>OpenApiMetadataRegistry</c> class carrying the flat intermediate metadata, so document generation
/// needs no runtime reflection. Invalid attribute combinations are reported as compiler diagnostics
/// whose ids match the runtime mapper's diagnostic codes.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class OpenApiMetadataGenerator : IIncrementalGenerator
{
    private const string AttributeNamespace = "Assimalign.Cohesion.OpenApi.Attributes";
    private const string OperationAttribute = AttributeNamespace + ".OpenApiOperationAttribute";
    private const string SchemaAttribute = AttributeNamespace + ".OpenApiSchemaAttribute";
    private const string SchemaComponentPrefix = "#/components/schemas/";

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var operations = context.SyntaxProvider
            .ForAttributeWithMetadataName(OperationAttribute,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => TransformOperation(ctx, ct))
            .Collect();

        var schemas = context.SyntaxProvider
            .ForAttributeWithMetadataName(SchemaAttribute,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => TransformSchema(ctx, ct))
            .Collect();

        var docLevel = context.CompilationProvider.Select(static (compilation, ct) => TransformDocLevel(compilation, ct));

        var combined = operations.Combine(schemas).Combine(docLevel);
        context.RegisterSourceOutput(combined, static (spc, data) => Emit(spc, data.Left.Left, data.Left.Right, data.Right));
    }

    // ---------------------------------------------------------------- operations

    private static GeneratedItem TransformOperation(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = context.TargetSymbol;
        var attribute = context.Attributes[0];
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var location = method.Locations.FirstOrDefault();

        var path = CtorString(attribute, 1) ?? string.Empty;
        var methodMember = EnumMemberName(attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0] : default);

        if (path.Length == 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(OpenApiGeneratorDiagnostics.MissingPath, location, method.Name));
        }

        var parameters = new List<string>();
        string? requestBody = null;
        var responses = new List<string>();
        var security = new List<string>();

        foreach (var member in method.GetAttributes())
        {
            var name = member.AttributeClass?.ToDisplayString();
            switch (name)
            {
                case AttributeNamespace + ".OpenApiParameterAttribute":
                    parameters.Add(BuildParameter(member, diagnostics, location));
                    break;
                case AttributeNamespace + ".OpenApiRequestBodyAttribute":
                    requestBody = BuildRequestBody(member, diagnostics, location, method.Name);
                    break;
                case AttributeNamespace + ".OpenApiResponseAttribute":
                    responses.Add(BuildResponse(member, diagnostics, location, method.Name));
                    break;
                case AttributeNamespace + ".OpenApiSecurityRequirementAttribute":
                    security.Add(BuildSecurityRequirement(member));
                    break;
            }
        }

        var builder = new StringBuilder();
        builder.Append($"new {Literals.MetadataNamespace}.OpenApiOperationMetadata {{ ");
        builder.Append($"Method = {Literals.Enum("OperationType", methodMember)}, ");
        builder.Append($"Path = {Literals.String(path)}, ");
        builder.Append($"OperationId = {Literals.String(NamedString(attribute, "OperationId"))}, ");
        builder.Append($"Summary = {Literals.String(NamedString(attribute, "Summary"))}, ");
        builder.Append($"Description = {Literals.String(NamedString(attribute, "Description"))}, ");
        builder.Append($"Deprecated = {Literals.Bool(NamedBool(attribute, "Deprecated"))}, ");
        builder.Append($"Tags = {StringArray(NamedStringArray(attribute, "Tags"))}, ");
        builder.Append($"Parameters = {Array("OpenApiParameterMetadata", parameters)}, ");
        builder.Append(requestBody is null ? "RequestBody = null, " : $"RequestBody = {requestBody}, ");
        builder.Append($"Responses = {Array("OpenApiResponseMetadata", responses)}, ");
        builder.Append($"Security = {Array("OpenApiSecurityRequirementMetadata", security)} }}");

        return new GeneratedItem(builder.ToString(), new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    private static string BuildParameter(AttributeData attribute, ImmutableArray<DiagnosticInfo>.Builder diagnostics, Location? location)
    {
        var name = CtorString(attribute, 0) ?? string.Empty;
        var inMember = EnumMemberName(attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1] : default);
        var required = NamedBool(attribute, "Required");

        if (inMember == "Path" && !required)
        {
            required = true;
            diagnostics.Add(DiagnosticInfo.Create(OpenApiGeneratorDiagnostics.PathParameterRequired, location, name));
        }

        var schemaType = SchemaTypeExpression(NamedEnumMember(attribute, "SchemaType"));

        return $"new {Literals.MetadataNamespace}.OpenApiParameterMetadata {{ "
            + $"Name = {Literals.String(name)}, In = {Literals.Enum("ParameterLocation", inMember)}, "
            + $"Description = {Literals.String(NamedString(attribute, "Description"))}, "
            + $"Required = {Literals.Bool(required)}, Deprecated = {Literals.Bool(NamedBool(attribute, "Deprecated"))}, "
            + $"SchemaType = {schemaType}, Format = {Literals.String(NamedString(attribute, "Format"))} }}";
    }

    private static string BuildRequestBody(AttributeData attribute, ImmutableArray<DiagnosticInfo>.Builder diagnostics, Location? location, string methodName)
    {
        var contentType = CtorString(attribute, 0) ?? "application/json";
        var schemaReference = ResolveSchemaReference(attribute, diagnostics, location, methodName);

        return $"new {Literals.MetadataNamespace}.OpenApiRequestBodyMetadata {{ "
            + $"ContentType = {Literals.String(contentType)}, "
            + $"Description = {Literals.String(NamedString(attribute, "Description"))}, "
            + $"Required = {Literals.Bool(NamedBool(attribute, "Required"))}, "
            + $"SchemaReference = {Literals.String(schemaReference)} }}";
    }

    private static string BuildResponse(AttributeData attribute, ImmutableArray<DiagnosticInfo>.Builder diagnostics, Location? location, string methodName)
    {
        var statusCode = ResponseStatusCode(attribute);
        var schemaReference = ResolveSchemaReference(attribute, diagnostics, location, methodName);

        return $"new {Literals.MetadataNamespace}.OpenApiResponseMetadata {{ "
            + $"StatusCode = {Literals.String(statusCode)}, "
            + $"Description = {Literals.String(NamedString(attribute, "Description"))}, "
            + $"ContentType = {Literals.String(NamedString(attribute, "ContentType"))}, "
            + $"SchemaReference = {Literals.String(schemaReference)} }}";
    }

    private static string BuildSecurityRequirement(AttributeData attribute)
    {
        var scheme = CtorString(attribute, 0) ?? string.Empty;
        var scopes = attribute.ConstructorArguments.Length > 1 ? EnumerateStringArray(attribute.ConstructorArguments[1]) : [];
        return $"new {Literals.MetadataNamespace}.OpenApiSecurityRequirementMetadata {{ "
            + $"Scheme = {Literals.String(scheme)}, Scopes = {StringArray(scopes)} }}";
    }

    // ------------------------------------------------------------------- schemas

    private static GeneratedItem TransformSchema(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var attribute = context.Attributes[0];

        var properties = new List<string>();
        foreach (var member in type.GetMembers())
        {
            if (member is not (IPropertySymbol or IFieldSymbol))
            {
                continue;
            }

            var propertyAttribute = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttributeNamespace + ".OpenApiSchemaPropertyAttribute");
            if (propertyAttribute is not null)
            {
                properties.Add(BuildSchemaProperty(propertyAttribute, member.Name));
            }
        }

        var initializer = $"new {Literals.MetadataNamespace}.OpenApiSchemaMetadata {{ "
            + $"Name = {Literals.String(NamedString(attribute, "Name") ?? type.Name)}, "
            + $"Title = {Literals.String(NamedString(attribute, "Title"))}, "
            + $"Description = {Literals.String(NamedString(attribute, "Description"))}, "
            + $"Type = {Literals.Enum("SchemaType", NamedEnumMember(attribute, "Type") ?? "Object")}, "
            + $"Deprecated = {Literals.Bool(NamedBool(attribute, "Deprecated"))}, "
            + $"Properties = {Array("OpenApiSchemaPropertyMetadata", properties)} }}";

        return new GeneratedItem(initializer, EquatableArray<DiagnosticInfo>.Empty);
    }

    private static string BuildSchemaProperty(AttributeData attribute, string memberName)
    {
        return $"new {Literals.MetadataNamespace}.OpenApiSchemaPropertyMetadata {{ "
            + $"Name = {Literals.String(NamedString(attribute, "Name") ?? memberName)}, "
            + $"Description = {Literals.String(NamedString(attribute, "Description"))}, "
            + $"Required = {Literals.Bool(NamedBool(attribute, "Required"))}, "
            + $"Nullable = {Literals.Bool(NamedBool(attribute, "Nullable"))}, "
            + $"SchemaType = {SchemaTypeExpression(NamedEnumMember(attribute, "SchemaType"))}, "
            + $"Format = {Literals.String(NamedString(attribute, "Format"))}, "
            + $"SchemaReference = {Literals.String(NamedString(attribute, "SchemaReference"))} }}";
    }

    // ---------------------------------------------------------------- doc level

    private static DocLevelItem TransformDocLevel(Compilation compilation, CancellationToken cancellationToken)
    {
        var tags = new List<string>();
        var schemes = new List<string>();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        void Scan(ISymbol owner)
        {
            foreach (var attribute in owner.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                if (name == AttributeNamespace + ".OpenApiTagAttribute")
                {
                    tags.Add(BuildTag(attribute));
                }
                else if (name == AttributeNamespace + ".OpenApiSecuritySchemeAttribute")
                {
                    schemes.Add(BuildSecurityScheme(attribute, diagnostics, owner.Locations.FirstOrDefault()));
                }
            }
        }

        Scan(compilation.Assembly);
        foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace, cancellationToken))
        {
            Scan(type);
        }

        return new DocLevelItem(
            new EquatableArray<string>(tags.ToImmutableArray()),
            new EquatableArray<string>(schemes.ToImmutableArray()),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    private static string BuildTag(AttributeData attribute)
    {
        var name = CtorString(attribute, 0) ?? string.Empty;
        return $"new {Literals.MetadataNamespace}.OpenApiTagMetadata {{ "
            + $"Name = {Literals.String(name)}, Description = {Literals.String(NamedString(attribute, "Description"))}, "
            + $"Summary = {Literals.String(NamedString(attribute, "Summary"))}, Parent = {Literals.String(NamedString(attribute, "Parent"))}, "
            + $"Kind = {Literals.String(NamedString(attribute, "Kind"))} }}";
    }

    private static string BuildSecurityScheme(AttributeData attribute, ImmutableArray<DiagnosticInfo>.Builder diagnostics, Location? location)
    {
        var name = CtorString(attribute, 0) ?? string.Empty;
        var typeMember = EnumMemberName(attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1] : default);
        var parameterName = NamedString(attribute, "ParameterName");
        var inMember = NamedEnumMember(attribute, "In");

        if (typeMember == "ApiKey" && (string.IsNullOrEmpty(parameterName) || inMember is null))
        {
            diagnostics.Add(DiagnosticInfo.Create(OpenApiGeneratorDiagnostics.IncompleteApiKey, location, name));
        }

        var inExpression = inMember is null ? "null" : $"({Literals.ModelNamespace}.ParameterLocation?){Literals.Enum("ParameterLocation", inMember)}";

        return $"new {Literals.MetadataNamespace}.OpenApiSecuritySchemeMetadata {{ "
            + $"Name = {Literals.String(name)}, Type = {Literals.Enum("SecuritySchemeType", typeMember)}, "
            + $"Description = {Literals.String(NamedString(attribute, "Description"))}, "
            + $"ParameterName = {Literals.String(parameterName)}, In = {inExpression}, "
            + $"Scheme = {Literals.String(NamedString(attribute, "Scheme"))}, BearerFormat = {Literals.String(NamedString(attribute, "BearerFormat"))}, "
            + $"OpenIdConnectUrl = {Literals.String(NamedString(attribute, "OpenIdConnectUrl"))} }}";
    }

    // ------------------------------------------------------------------- emit

    private static void Emit(SourceProductionContext context, ImmutableArray<GeneratedItem> operations, ImmutableArray<GeneratedItem> schemas, DocLevelItem docLevel)
    {
        foreach (var operation in operations)
        {
            ReportAll(context, operation.Diagnostics);
        }

        foreach (var schema in schemas)
        {
            ReportAll(context, schema.Diagnostics);
        }

        ReportAll(context, docLevel.Diagnostics);

        if (operations.IsEmpty && schemas.IsEmpty && docLevel.Tags.Count == 0 && docLevel.SecuritySchemes.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("namespace Assimalign.Cohesion.OpenApi.Generated");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>The OpenApi metadata discovered from attributes at compile time.</summary>");
        builder.AppendLine("    public static class OpenApiMetadataRegistry");
        builder.AppendLine("    {");
        AppendCollection(builder, "OpenApiOperationMetadata", "Operations", operations.Select(o => o.Initializer));
        AppendCollection(builder, "OpenApiSchemaMetadata", "Schemas", schemas.Select(s => s.Initializer));
        AppendCollection(builder, "OpenApiTagMetadata", "Tags", docLevel.Tags);
        AppendCollection(builder, "OpenApiSecuritySchemeMetadata", "SecuritySchemes", docLevel.SecuritySchemes);
        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("OpenApiMetadataRegistry.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void AppendCollection(StringBuilder builder, string metadataType, string propertyName, IEnumerable<string> initializers)
    {
        var items = initializers.ToList();
        builder.AppendLine($"        /// <summary>The discovered {propertyName.ToLowerInvariant()}.</summary>");
        builder.Append($"        public static global::System.Collections.Generic.IReadOnlyList<{Literals.MetadataNamespace}.{metadataType}> {propertyName} {{ get; }} = ");
        if (items.Count == 0)
        {
            builder.AppendLine($"global::System.Array.Empty<{Literals.MetadataNamespace}.{metadataType}>();");
            return;
        }

        builder.AppendLine($"new {Literals.MetadataNamespace}.{metadataType}[]");
        builder.AppendLine("        {");
        foreach (var item in items)
        {
            builder.AppendLine($"            {item},");
        }

        builder.AppendLine("        };");
    }

    private static void ReportAll(SourceProductionContext context, EquatableArray<DiagnosticInfo> diagnostics)
    {
        foreach (var info in diagnostics)
        {
            context.ReportDiagnostic(info.ToDiagnostic(OpenApiGeneratorDiagnostics.GetDescriptor(info.DescriptorId)));
        }
    }

    // --------------------------------------------------------------- extraction

    private static string ResolveSchemaReference(AttributeData attribute, ImmutableArray<DiagnosticInfo>.Builder diagnostics, Location? location, string methodName)
    {
        var modelType = NamedType(attribute, "ModelType");
        var explicitReference = NamedString(attribute, "SchemaReference");

        if (modelType is not null && explicitReference is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(OpenApiGeneratorDiagnostics.AmbiguousSchema, location, methodName));
            return explicitReference;
        }

        if (explicitReference is not null)
        {
            return explicitReference;
        }

        return modelType is not null ? SchemaComponentPrefix + modelType.Name : null!;
    }

    private static string ResponseStatusCode(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length > 0)
        {
            var argument = attribute.ConstructorArguments[0];
            if (argument.Value is int code)
            {
                return code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (argument.Value is string text)
            {
                return text;
            }
        }

        return "default";
    }

    private static string SchemaTypeExpression(string? schemaKindMember)
    {
        if (schemaKindMember is null or "Unspecified")
        {
            return "null";
        }

        return $"({Literals.ModelNamespace}.SchemaType?){Literals.Enum("SchemaType", schemaKindMember)}";
    }

    private static string Array(string metadataType, List<string> items)
    {
        if (items.Count == 0)
        {
            return $"global::System.Array.Empty<{Literals.MetadataNamespace}.{metadataType}>()";
        }

        return $"new {Literals.MetadataNamespace}.{metadataType}[] {{ {string.Join(", ", items)} }}";
    }

    private static string StringArray(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return "global::System.Array.Empty<string>()";
        }

        return $"new string[] {{ {string.Join(", ", values.Select(Literals.String))} }}";
    }

    private static string? CtorString(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index ? attribute.ConstructorArguments[index].Value as string : null;

    private static string? NamedString(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name)
            {
                return pair.Value.Value as string;
            }
        }

        return null;
    }

    private static bool NamedBool(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name && pair.Value.Value is bool value)
            {
                return value;
            }
        }

        return false;
    }

    private static string? NamedEnumMember(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name)
            {
                return EnumMemberName(pair.Value);
            }
        }

        return null;
    }

    private static ITypeSymbol? NamedType(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name)
            {
                return pair.Value.Value as ITypeSymbol;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> NamedStringArray(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name)
            {
                return EnumerateStringArray(pair.Value);
            }
        }

        return [];
    }

    private static IReadOnlyList<string> EnumerateStringArray(TypedConstant constant)
    {
        if (constant.Kind != TypedConstantKind.Array || constant.IsNull)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var element in constant.Values)
        {
            if (element.Value is string text)
            {
                result.Add(text);
            }
        }

        return result;
    }

    private static string EnumMemberName(TypedConstant constant)
    {
        if (constant.Type is INamedTypeSymbol enumType && constant.Value is not null)
        {
            foreach (var member in enumType.GetMembers())
            {
                if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, constant.Value))
                {
                    return field.Name;
                }
            }
        }

        return constant.Value?.ToString() ?? string.Empty;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol root, CancellationToken cancellationToken)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            foreach (var member in current.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    stack.Push(childNamespace);
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var nested in type.GetTypeMembers())
                    {
                        stack.Push(nested);
                    }
                }
            }
        }
    }

    private readonly record struct GeneratedItem(string Initializer, EquatableArray<DiagnosticInfo> Diagnostics);

    private readonly record struct DocLevelItem(EquatableArray<string> Tags, EquatableArray<string> SecuritySchemes, EquatableArray<DiagnosticInfo> Diagnostics);
}
