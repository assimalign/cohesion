using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using Assimalign.Cohesion.OpenApi.SourceGeneration.Internal;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration;

/// <summary>
/// Discovers the OpenApi authoring attributes at compile time and emits their flat intermediate
/// metadata, so document generation needs no runtime reflection. An annotated assembly gets a public
/// provider class with an assembly-unique name, advertised to referencing compilations by
/// <c>[assembly: OpenApiMetadataProvider]</c>. Every compilation that has metadata of its own or
/// references an advertised provider gets an internal <c>OpenApiMetadataRegistry</c> that composes all of
/// them. Invalid attribute combinations are reported as compiler diagnostics whose ids match the runtime
/// mapper's diagnostic codes.
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

        // Assembly attributes on referenced binaries are metadata, not syntax, so the advertised
        // providers are read from the compilation's assembly symbols rather than a syntax provider.
        var providers = context.CompilationProvider.Select(static (compilation, ct) => TransformProviders(compilation, ct));

        var combined = operations.Combine(schemas).Combine(docLevel).Combine(providers);
        context.RegisterSourceOutput(combined, static (spc, data) => Emit(spc, data.Left.Left.Left, data.Left.Left.Right, data.Left.Right, data.Right));
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

    // ---------------------------------------------------------------- providers

    private static ProvidersItem TransformProviders(Compilation compilation, CancellationToken cancellationToken)
    {
        var assemblyName = compilation.AssemblyName ?? string.Empty;
        var providerAttribute = compilation.GetTypeByMetadataName(MetadataProviderNames.AttributeMetadataName);
        var providerInterface = compilation.GetTypeByMetadataName(MetadataProviderNames.InterfaceMetadataName);
        if (providerAttribute is null || providerInterface is null)
        {
            return new ProvidersItem(assemblyName, EquatableArray<ProviderReference>.Empty, EquatableArray<ProviderReference>.Empty, EquatableArray<DiagnosticInfo>.Empty);
        }

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        // A hand-written provider this compilation advertises joins its own registry too; the generated
        // provider is not visible here, because a generator never sees its own output.
        var own = new List<ProviderReference>();
        CollectProviders(compilation.Assembly, providerAttribute, providerInterface, own, diagnostics);

        var referenced = new List<ProviderReference>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CouldAdvertiseProviders(assembly))
            {
                CollectProviders(assembly, providerAttribute, providerInterface, referenced, diagnostics);
            }
        }

        return new ProvidersItem(
            assemblyName,
            Order(own),
            Order(referenced),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    private static void CollectProviders(
        IAssemblySymbol assembly,
        INamedTypeSymbol providerAttribute,
        INamedTypeSymbol providerInterface,
        List<ProviderReference> providers,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, providerAttribute))
            {
                continue;
            }

            if (TryResolveProvider(attribute, assembly, providerInterface, out var typeName, out var problem))
            {
                providers.Add(new ProviderReference(assembly.Identity.Name, typeName));
                continue;
            }

            // A referenced assembly's attribute has no syntax, so only this compilation's own
            // declarations carry a source location.
            var syntax = attribute.ApplicationSyntaxReference;
            var location = syntax is null ? null : Location.Create(syntax.SyntaxTree, syntax.Span);
            diagnostics.Add(DiagnosticInfo.Create(OpenApiGeneratorDiagnostics.UnusableProvider, location, problem));
        }
    }

    private static bool TryResolveProvider(
        AttributeData attribute,
        IAssemblySymbol assembly,
        INamedTypeSymbol providerInterface,
        out string typeName,
        out string problem)
    {
        typeName = string.Empty;
        var assemblyName = assembly.Identity.Name;

        if (attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0] is not { Kind: TypedConstantKind.Type, Value: INamedTypeSymbol type }
            || type.TypeKind == TypeKind.Error)
        {
            problem = $"An [OpenApiMetadataProvider] on assembly '{assemblyName}' was skipped because it does not name a resolvable provider type.";
            return false;
        }

        var reason = ProviderShapeProblem(type, assembly, providerInterface);
        if (reason is not null)
        {
            problem = $"The OpenApi metadata provider '{type.ToDisplayString()}' advertised by assembly '{assemblyName}' was skipped because {reason}.";
            return false;
        }

        typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        problem = string.Empty;
        return true;
    }

    private static string? ProviderShapeProblem(INamedTypeSymbol type, IAssemblySymbol assembly, INamedTypeSymbol providerInterface)
    {
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly))
        {
            return "it is declared in another assembly; an assembly advertises only its own providers";
        }

        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic)
        {
            return "it is not a non-abstract class";
        }

        if (IsOpenGeneric(type))
        {
            return "it is an open generic type";
        }

        if (!IsExternallyVisible(type))
        {
            return "it is not public, so a referencing assembly cannot construct it";
        }

        if (!type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, providerInterface)))
        {
            return "it does not implement IOpenApiMetadataProvider";
        }

        if (!type.InstanceConstructors.Any(static constructor => constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0))
        {
            return "it has no public parameterless constructor";
        }

        return null;
    }

    private static bool CouldAdvertiseProviders(IAssemblySymbol assembly)
    {
        // The attribute type lives in Attributes, so only an assembly that references Attributes can
        // carry it. Checking the reference list first skips decoding every framework assembly's attributes.
        if (assembly.Identity.Name == MetadataProviderNames.AttributesAssemblyName)
        {
            return false;
        }

        foreach (var module in assembly.Modules)
        {
            foreach (var identity in module.ReferencedAssemblies)
            {
                if (identity.Name == MetadataProviderNames.AttributesAssemblyName)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsExternallyVisible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOpenGeneric(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsUnboundGenericType || current.TypeArguments.Any(static argument => argument.TypeKind == TypeKind.TypeParameter))
            {
                return true;
            }
        }

        return false;
    }

    private static EquatableArray<ProviderReference> Order(List<ProviderReference> providers) =>
        new(providers
            .Distinct()
            .OrderBy(static provider => provider.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static provider => provider.TypeName, StringComparer.Ordinal)
            .ToImmutableArray());

    // ------------------------------------------------------------------- emit

    private static void Emit(SourceProductionContext context, ImmutableArray<GeneratedItem> operations, ImmutableArray<GeneratedItem> schemas, DocLevelItem docLevel, ProvidersItem providers)
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
        ReportAll(context, providers.Diagnostics);

        var hasOwnMetadata = !operations.IsEmpty || !schemas.IsEmpty || docLevel.Tags.Count > 0 || docLevel.SecuritySchemes.Count > 0;
        if (!hasOwnMetadata && providers.Own.Count == 0 && providers.Referenced.Count == 0)
        {
            return;
        }

        var ownProvider = MetadataProviderNames.TypeName(providers.AssemblyName);
        var ownProviderReference = $"global::{MetadataProviderNames.Namespace}.{ownProvider}";

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();

        if (hasOwnMetadata)
        {
            builder.AppendLine($"[assembly: {Literals.MetadataNamespace}.OpenApiMetadataProviderAttribute(typeof({ownProviderReference}))]");
            builder.AppendLine();
        }

        builder.AppendLine($"namespace {MetadataProviderNames.Namespace}");
        builder.AppendLine("{");

        if (hasOwnMetadata)
        {
            AppendProvider(builder, providers.AssemblyName, ownProvider, operations, schemas, docLevel);
            builder.AppendLine();
        }

        var composed = new List<(string TypeName, string AssemblyName)>();
        composed.AddRange(providers.Referenced.Select(static provider => (provider.TypeName, provider.AssemblyName)));
        composed.AddRange(providers.Own.Select(static provider => (provider.TypeName, provider.AssemblyName)));
        if (hasOwnMetadata)
        {
            composed.Add((ownProviderReference, providers.AssemblyName));
        }

        AppendRegistry(builder, composed);
        builder.AppendLine("}");

        context.AddSource("OpenApiMetadataRegistry.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void AppendProvider(StringBuilder builder, string assemblyName, string typeName, ImmutableArray<GeneratedItem> operations, ImmutableArray<GeneratedItem> schemas, DocLevelItem docLevel)
    {
        builder.AppendLine("    /// <summary>");
        builder.AppendLine($"    /// The OpenApi metadata the <c>{EscapeXml(assemblyName)}</c> assembly declares through attributes, discovered at compile time.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine("    /// Generated and advertised by <c>[assembly: OpenApiMetadataProvider]</c>, so a referencing compilation composes it");
        builder.AppendLine("    /// into its own <c>OpenApiMetadataRegistry</c>. Code in this assembly reads the composed registry, not this type.");
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        builder.AppendLine($"    public sealed class {typeName} : {Literals.MetadataNamespace}.IOpenApiMetadataProvider");
        builder.AppendLine("    {");
        AppendArrayField(builder, "OpenApiOperationMetadata", "_operations", operations.Select(static operation => operation.Initializer));
        AppendArrayField(builder, "OpenApiSchemaMetadata", "_schemas", schemas.Select(static schema => schema.Initializer));
        AppendArrayField(builder, "OpenApiTagMetadata", "_tags", docLevel.Tags);
        AppendArrayField(builder, "OpenApiSecuritySchemeMetadata", "_securitySchemes", docLevel.SecuritySchemes);
        AppendProviderProperty(builder, "OpenApiOperationMetadata", "Operations", "_operations");
        builder.AppendLine();
        AppendProviderProperty(builder, "OpenApiSchemaMetadata", "Schemas", "_schemas");
        builder.AppendLine();
        AppendProviderProperty(builder, "OpenApiTagMetadata", "Tags", "_tags");
        builder.AppendLine();
        AppendProviderProperty(builder, "OpenApiSecuritySchemeMetadata", "SecuritySchemes", "_securitySchemes");
        builder.AppendLine("    }");
    }

    private static void AppendArrayField(StringBuilder builder, string metadataType, string fieldName, IEnumerable<string> initializers)
    {
        var items = initializers.ToList();
        builder.Append($"        private static readonly {Literals.MetadataNamespace}.{metadataType}[] {fieldName} = ");
        if (items.Count == 0)
        {
            builder.AppendLine($"global::System.Array.Empty<{Literals.MetadataNamespace}.{metadataType}>();");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"new {Literals.MetadataNamespace}.{metadataType}[]");
        builder.AppendLine("        {");
        foreach (var item in items)
        {
            builder.AppendLine($"            {item},");
        }

        builder.AppendLine("        };");
        builder.AppendLine();
    }

    private static void AppendProviderProperty(StringBuilder builder, string metadataType, string propertyName, string fieldName)
    {
        builder.AppendLine("        /// <inheritdoc/>");
        builder.AppendLine($"        public global::System.Collections.Generic.IReadOnlyList<{Literals.MetadataNamespace}.{metadataType}> {propertyName} => {fieldName};");
    }

    private static void AppendRegistry(StringBuilder builder, List<(string TypeName, string AssemblyName)> composed)
    {
        var provider = $"{Literals.MetadataNamespace}.IOpenApiMetadataProvider";

        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// The OpenApi metadata of this compilation: every provider a referenced assembly advertises, ordered by");
        builder.AppendLine("    /// assembly name, followed by this assembly's own. Each assembly composes its own internal registry.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    internal static class OpenApiMetadataRegistry");
        builder.AppendLine("    {");
        builder.AppendLine($"        private static readonly {provider}[] _providers = new {provider}[]");
        builder.AppendLine("        {");
        foreach (var (typeName, assemblyName) in composed)
        {
            builder.AppendLine($"            new {typeName}(), // {CommentText(assemblyName)}");
        }

        builder.AppendLine("        };");
        builder.AppendLine();
        builder.AppendLine("        /// <summary>Gets the composed providers, in composition order.</summary>");
        builder.AppendLine($"        public static global::System.Collections.Generic.IReadOnlyList<{provider}> Providers => _providers;");
        builder.AppendLine();
        AppendRegistryProperty(builder, "OpenApiOperationMetadata", "Operations");
        AppendRegistryProperty(builder, "OpenApiSchemaMetadata", "Schemas");
        AppendRegistryProperty(builder, "OpenApiTagMetadata", "Tags");
        AppendRegistryProperty(builder, "OpenApiSecuritySchemeMetadata", "SecuritySchemes");
        builder.AppendLine($"        private static T[] Combine<T>(global::System.Func<{provider}, global::System.Collections.Generic.IReadOnlyList<T>> select)");
        builder.AppendLine("        {");
        builder.AppendLine("            var items = new global::System.Collections.Generic.List<T>();");
        builder.AppendLine("            foreach (var provider in _providers)");
        builder.AppendLine("            {");
        builder.AppendLine("                items.AddRange(select(provider));");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            return items.ToArray();");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static void AppendRegistryProperty(StringBuilder builder, string metadataType, string propertyName)
    {
        builder.AppendLine($"        /// <summary>Gets the {Describe(propertyName)} of every composed provider, in provider order.</summary>");
        builder.AppendLine($"        public static global::System.Collections.Generic.IReadOnlyList<{Literals.MetadataNamespace}.{metadataType}> {propertyName} {{ get; }} = Combine(provider => provider.{propertyName});");
        builder.AppendLine();
    }

    private static string Describe(string propertyName) => propertyName switch
    {
        "SecuritySchemes" => "security schemes",
        "Schemas" => "schema components",
        _ => propertyName.ToLowerInvariant()
    };

    private static string EscapeXml(string value) => CommentText(value)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// Makes an assembly name safe inside a single-line comment: a control character, or a line or
    /// paragraph separator that C# treats as a new line, would end the comment early.
    /// </summary>
    private static string CommentText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            // 0x2028 and 0x2029 are the Unicode line and paragraph separators.
            builder.Append(char.IsControl(character) || character == (char)0x2028 || character == (char)0x2029 ? '?' : character);
        }

        return builder.ToString();
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

    /// <summary>
    /// The providers a compilation composes: <see cref="Own"/> holds the hand-written providers this
    /// assembly advertises, <see cref="Referenced"/> those its references advertise, each ordered by
    /// assembly name and then fully qualified type name.
    /// </summary>
    private readonly record struct ProvidersItem(
        string AssemblyName,
        EquatableArray<ProviderReference> Own,
        EquatableArray<ProviderReference> Referenced,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private readonly record struct ProviderReference(string AssemblyName, string TypeName);
}
