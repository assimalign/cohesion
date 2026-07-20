using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.SourceGeneration.Web;

/// <summary>
/// Emits AOT-safe binding thunks for the typed <c>Map*</c> endpoint overloads on
/// <c>Assimalign.Cohesion.Web.WebApplicationPipelineBuilderExtensions</c>. Each typed call site — for
/// example <c>app.MapGet("/users/{id}", (int id, IHttpContext context) =&gt; ...)</c> — is intercepted
/// with a C# interceptor that casts the handler back to its concrete delegate type, binds each
/// parameter from the request (route / query / header / body / form, plus direct injections), and
/// invokes the handler directly. No reflection and no expression compilation happen at run time.
/// Call sites the generator cannot model are left untouched, so the placeholder overload throws.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EndpointBindingGenerator : IIncrementalGenerator
{
    private const string WebNamespace = "Assimalign.Cohesion.Web";
    private const string GeneratedNamespace = "Assimalign.Cohesion.Web.Api.Generated";

    private static readonly SymbolDisplayFormat FullyQualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly HashSet<string> Verbs = new()
    {
        "Map", "MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete"
    };

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var endpoints = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidate(node),
                transform: static (ctx, ct) => Transform(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!.Value)
            .Collect();

        context.RegisterImplementationSourceOutput(endpoints, static (spc, models) => Emit(spc, models));
    }

    private static bool IsCandidate(SyntaxNode node)
        => node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax memberAccess,
            ArgumentList.Arguments.Count: >= 2
        }
        && Verbs.Contains(memberAccess.Name.Identifier.Text);

    private static EndpointBinding? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        SemanticModel model = ctx.SemanticModel;
        Compilation compilation = model.Compilation;

        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        if (!Verbs.Contains(method.Name))
        {
            return null;
        }

        // Identify the typed (System.Delegate) overload; the WebApplicationMiddleware overloads are
        // registered verbatim and are not our concern.
        int delegateParameterIndex = -1;
        int patternParameterIndex = -1;
        int validatorParameterIndex = -1;
        bool hasMethodParameter = false;

        INamedTypeSymbol? validatorType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.ObjectValidation.IValidator");
        INamedTypeSymbol? httpMethodType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.Http.HttpMethod");

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            ITypeSymbol parameterType = method.Parameters[i].Type;

            if (parameterType.ToDisplayString() == "System.Delegate")
            {
                delegateParameterIndex = i;
            }
            else if (parameterType.SpecialType == SpecialType.System_String)
            {
                patternParameterIndex = i;
            }
            else if (httpMethodType is not null && SymbolEqualityComparer.Default.Equals(parameterType, httpMethodType))
            {
                hasMethodParameter = true;
            }
            else if (validatorType is not null && SymbolEqualityComparer.Default.Equals(parameterType, validatorType))
            {
                validatorParameterIndex = i;
            }
        }

        if (delegateParameterIndex < 0 || patternParameterIndex < 0)
        {
            return null;
        }

        // The extension symbol should be the Web.Api mapping helper.
        if (method.ContainingNamespace?.ToDisplayString() is { } containingNamespace
            && !containingNamespace.StartsWith(WebNamespace, System.StringComparison.Ordinal))
        {
            return null;
        }

        SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

        if (delegateParameterIndex >= arguments.Count || patternParameterIndex >= arguments.Count)
        {
            return null;
        }

        // The handler lambda / method group.
        ExpressionSyntax handlerExpression = arguments[delegateParameterIndex].Expression;

        if (model.GetSymbolInfo(handlerExpression, ct).Symbol is not IMethodSymbol handler)
        {
            return null;
        }

        // Return shape (middleware-first: no result types).
        if (!TryGetReturnKind(handler.ReturnType, compilation, out ReturnKind returnKind))
        {
            return null;
        }

        // Route tokens from a literal pattern power name-based route inference.
        HashSet<string> routeTokens = new(System.StringComparer.OrdinalIgnoreCase);
        if (arguments[patternParameterIndex].Expression is LiteralExpressionSyntax { Token.Value: string patternText })
        {
            CollectRouteTokens(patternText, routeTokens);
        }

        INamedTypeSymbol? parsableType = compilation.GetTypeByMetadataName("System.IParsable`1");
        INamedTypeSymbol? contextType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.Http.IHttpContext");
        INamedTypeSymbol? cancellationType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        INamedTypeSymbol? featureType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.Http.IHttpFeature");

        var parameters = ImmutableArray.CreateBuilder<ParameterBinding>(handler.Parameters.Length);
        int bodyParameterIndex = -1;
        bool usesForm = false;

        for (int i = 0; i < handler.Parameters.Length; i++)
        {
            if (!TryClassify(
                    handler.Parameters[i],
                    routeTokens,
                    parsableType,
                    contextType,
                    cancellationType,
                    featureType,
                    out ParameterBinding binding))
            {
                return null;
            }

            if (binding.Source == BindingSource.Body)
            {
                if (bodyParameterIndex >= 0)
                {
                    return null; // at most one body parameter
                }

                bodyParameterIndex = i;
            }
            else if (binding.Source == BindingSource.Form)
            {
                usesForm = true;
            }

            parameters.Add(binding);
        }

        if (bodyParameterIndex >= 0 && usesForm)
        {
            return null; // body and form are mutually exclusive
        }

        InterceptableLocation? location = model.GetInterceptableLocation(invocation, ct);
        if (location is null)
        {
            return null;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax receiverAccess
            || model.GetTypeInfo(receiverAccess.Expression, ct).Type is not ITypeSymbol receiverType)
        {
            return null;
        }

        string methodExpression = hasMethodParameter
            ? "method"
            : "global::Assimalign.Cohesion.Http.HttpMethod." + VerbToMethod(method.Name);

        string delegateType = BuildDelegateType(parameters, returnKind);

        return new EndpointBinding(
            location.GetInterceptsLocationAttributeSyntax(),
            receiverType.ToDisplayString(FullyQualified),
            hasMethodParameter,
            methodExpression,
            validatorParameterIndex >= 0,
            delegateType,
            returnKind,
            new EquatableArray<ParameterBinding>(parameters.ToImmutable()),
            bodyParameterIndex,
            usesForm);
    }

    private static bool TryClassify(
        IParameterSymbol parameter,
        HashSet<string> routeTokens,
        INamedTypeSymbol? parsableType,
        INamedTypeSymbol? contextType,
        INamedTypeSymbol? cancellationType,
        INamedTypeSymbol? featureType,
        out ParameterBinding binding)
    {
        binding = default;
        ITypeSymbol type = parameter.Type;
        string declaredType = type.ToDisplayString(FullyQualified);

        // Direct injections take precedence over any binding source.
        if (contextType is not null && SymbolEqualityComparer.Default.Equals(type, contextType))
        {
            binding = new ParameterBinding(declaredType, "", "", BindingSource.Context, ConversionKind.Injection, "", false);
            return true;
        }

        if (cancellationType is not null && SymbolEqualityComparer.Default.Equals(type, cancellationType))
        {
            binding = new ParameterBinding(declaredType, "", "", BindingSource.Cancellation, ConversionKind.Injection, "", false);
            return true;
        }

        if (featureType is not null && ImplementsInterface(type, featureType))
        {
            binding = new ParameterBinding(declaredType, "", declaredType, BindingSource.Feature, ConversionKind.Injection, "", false);
            return true;
        }

        (ConversionKind conversion, string coreType, bool required) = ClassifyConversion(type, parsableType);

        BindingSource? explicitSource = GetExplicitSource(parameter, out string? explicitName);
        string key = string.IsNullOrEmpty(explicitName) ? parameter.Name : explicitName!;

        BindingSource source;
        if (explicitSource is { } declared)
        {
            source = declared;
        }
        else if (conversion == ConversionKind.Complex)
        {
            source = BindingSource.Body;
        }
        else if (routeTokens.Contains(parameter.Name))
        {
            source = BindingSource.Route;
        }
        else
        {
            source = BindingSource.Query;
        }

        // A complex type cannot be bound from a scalar source, and body binding always reads the model.
        if (source == BindingSource.Body)
        {
            conversion = ConversionKind.Complex;
        }
        else if (conversion == ConversionKind.Complex)
        {
            return false; // e.g. [FromQuery] on a complex type
        }

        binding = new ParameterBinding(declaredType, coreType, "", source, conversion, key, required);
        return true;
    }

    private static (ConversionKind Conversion, string CoreType, bool Required) ClassifyConversion(ITypeSymbol type, INamedTypeSymbol? parsableType)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            bool required = type.NullableAnnotation != NullableAnnotation.Annotated;
            return (ConversionKind.String, "", required);
        }

        if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            ITypeSymbol inner = named.TypeArguments[0];
            string innerType = inner.ToDisplayString(FullyQualified);

            if (inner.TypeKind == TypeKind.Enum)
            {
                return (ConversionKind.NullableEnum, innerType, false);
            }

            if (ImplementsParsable(inner, parsableType))
            {
                return (ConversionKind.NullableParsable, innerType, false);
            }

            return (ConversionKind.Complex, "", false);
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return (ConversionKind.Enum, type.ToDisplayString(FullyQualified), true);
        }

        if (ImplementsParsable(type, parsableType))
        {
            return (ConversionKind.Parsable, type.ToDisplayString(FullyQualified), true);
        }

        return (ConversionKind.Complex, "", false);
    }

    private static BindingSource? GetExplicitSource(IParameterSymbol parameter, out string? name)
    {
        name = null;

        foreach (AttributeData attribute in parameter.GetAttributes())
        {
            string? attributeName = attribute.AttributeClass?.Name;
            BindingSource? source = attributeName switch
            {
                "FromRouteAttribute" => BindingSource.Route,
                "FromQueryAttribute" => BindingSource.Query,
                "FromHeaderAttribute" => BindingSource.Header,
                "FromBodyAttribute" => BindingSource.Body,
                "FromFormAttribute" => BindingSource.Form,
                _ => null
            };

            if (source is null)
            {
                continue;
            }

            if (attribute.AttributeClass?.ContainingNamespace?.ToDisplayString() != WebNamespace)
            {
                continue;
            }

            foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
            {
                if (named.Key == "Name" && named.Value.Value is string value)
                {
                    name = value;
                }
            }

            return source;
        }

        return null;
    }

    private static bool TryGetReturnKind(ITypeSymbol returnType, Compilation compilation, out ReturnKind kind)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            kind = ReturnKind.Void;
            return true;
        }

        INamedTypeSymbol? task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        INamedTypeSymbol? valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");

        if (task is not null && SymbolEqualityComparer.Default.Equals(returnType, task))
        {
            kind = ReturnKind.Task;
            return true;
        }

        if (valueTask is not null && SymbolEqualityComparer.Default.Equals(returnType, valueTask))
        {
            kind = ReturnKind.ValueTask;
            return true;
        }

        kind = default;
        return false; // Task<T>/ValueTask<T> and other returns are out of scope (no result types)
    }

    private static string BuildDelegateType(IReadOnlyList<ParameterBinding> parameters, ReturnKind returnKind)
    {
        var typeArguments = new List<string>(parameters.Count + 1);
        foreach (ParameterBinding parameter in parameters)
        {
            typeArguments.Add(parameter.DeclaredType);
        }

        if (returnKind == ReturnKind.Void)
        {
            return typeArguments.Count == 0
                ? "global::System.Action"
                : "global::System.Action<" + string.Join(", ", typeArguments) + ">";
        }

        typeArguments.Add(returnKind == ReturnKind.Task
            ? "global::System.Threading.Tasks.Task"
            : "global::System.Threading.Tasks.ValueTask");

        return "global::System.Func<" + string.Join(", ", typeArguments) + ">";
    }

    // ---------------------------------------------------------------------
    // Emit
    // ---------------------------------------------------------------------

    private static void Emit(SourceProductionContext spc, ImmutableArray<EndpointBinding> models)
    {
        if (models.IsDefaultOrEmpty)
        {
            return;
        }

        bool anyBody = models.Any(static model => model.BodyParameterIndex >= 0);

        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#pragma warning disable CS1998");
        builder.AppendLine("#pragma warning disable CS8600");
        builder.AppendLine("#pragma warning disable CS8601");
        builder.AppendLine("#pragma warning disable CS8602");
        builder.AppendLine("#pragma warning disable CS8604");
        builder.AppendLine();
        builder.AppendLine("using Assimalign.Cohesion.Http;");
        builder.AppendLine("using Assimalign.Cohesion.Web;");
        builder.AppendLine("using Assimalign.Cohesion.Web.Routing;");
        if (anyBody)
        {
            builder.AppendLine("using Assimalign.Cohesion.Web.Serialization;");
        }

        builder.AppendLine();
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
        builder.AppendLine("    file static class EndpointBindingInterceptors");
        builder.AppendLine("    {");

        for (int i = 0; i < models.Length; i++)
        {
            EmitEndpoint(builder, models[i], i);
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        spc.AddSource("EndpointBinding.Interceptors.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void EmitEndpoint(StringBuilder builder, EndpointBinding model, int index)
    {
        if (index > 0)
        {
            builder.AppendLine();
        }

        builder.Append("        ").AppendLine(model.InterceptsAttribute);
        builder.Append("        public static global::Assimalign.Cohesion.Web.IWebApplicationPipelineBuilder Intercept_")
            .Append(index)
            .Append("(this ")
            .Append(model.ReceiverType)
            .Append(" builder, ");

        if (model.HasMethodParameter)
        {
            builder.Append("global::Assimalign.Cohesion.Http.HttpMethod method, ");
        }

        builder.Append("string pattern, global::System.Delegate handler");

        if (model.HasValidator)
        {
            builder.Append(", global::Assimalign.Cohesion.ObjectValidation.IValidator validator");
        }

        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.Append("            var __handler = (").Append(model.DelegateType).AppendLine(")handler;");
        builder.Append("            return builder.Map(")
            .Append(model.MethodExpression)
            .AppendLine(", pattern, async (global::Assimalign.Cohesion.Http.IHttpContext context) =>");
        builder.AppendLine("            {");

        EmitThunkBody(builder, model, "                ");

        builder.Append("            }");

        if (model.HasValidator)
        {
            builder.AppendLine(",");
            builder.AppendLine("            new global::Assimalign.Cohesion.Web.Routing.Metadata.RouterRouteMetadataCollection(new global::Assimalign.Cohesion.Web.EndpointValidationMetadata(validator)));");
        }
        else
        {
            builder.AppendLine(");");
        }

        builder.AppendLine("        }");
    }

    private static void EmitThunkBody(StringBuilder builder, EndpointBinding model, string indent)
    {
        if (model.UsesForm)
        {
            builder.Append(indent).AppendLine("global::Assimalign.Cohesion.Http.IHttpFormCollection __form = await context.ReadFormAsync(context.RequestCancelled);");
        }

        var parameters = model.Parameters;

        for (int i = 0; i < parameters.Count; i++)
        {
            EmitParameter(builder, parameters[i], i, indent);
        }

        if (model.HasValidator && model.BodyParameterIndex >= 0)
        {
            builder.Append(indent).AppendLine("{");
            builder.Append(indent).Append("    global::Assimalign.Cohesion.ObjectValidation.ValidationResult __validation = validator.Validate(__arg")
                .Append(model.BodyParameterIndex).AppendLine(");");
            builder.Append(indent).AppendLine("    if (!__validation.IsValid)");
            builder.Append(indent).AppendLine("    {");
            builder.Append(indent).AppendLine("        await global::Assimalign.Cohesion.Web.EndpointValidation.WriteProblemAsync(context, __validation, context.RequestCancelled);");
            builder.Append(indent).AppendLine("        return;");
            builder.Append(indent).AppendLine("    }");
            builder.Append(indent).AppendLine("}");
        }

        string arguments = string.Join(", ", Enumerable.Range(0, parameters.Count).Select(static i => "__arg" + i));
        string awaitKeyword = model.Return == ReturnKind.Void ? string.Empty : "await ";

        builder.Append(indent).Append(awaitKeyword).Append("__handler(").Append(arguments).AppendLine(");");
    }

    private static void EmitParameter(StringBuilder builder, ParameterBinding parameter, int index, string indent)
    {
        switch (parameter.Source)
        {
            case BindingSource.Context:
                builder.Append(indent).Append("global::Assimalign.Cohesion.Http.IHttpContext __arg").Append(index).AppendLine(" = context;");
                return;

            case BindingSource.Cancellation:
                builder.Append(indent).Append("global::System.Threading.CancellationToken __arg").Append(index).AppendLine(" = context.RequestCancelled;");
                return;

            case BindingSource.Feature:
                builder.Append(indent).Append(parameter.FeatureType).Append(" __arg").Append(index)
                    .Append(" = context.Features.Get<").Append(parameter.FeatureType).AppendLine(">();");
                return;

            case BindingSource.Body:
                EmitBody(builder, parameter, index, indent);
                return;

            case BindingSource.Route:
                EmitRoute(builder, parameter, index, indent);
                return;

            default:
                EmitScalar(builder, parameter, index, indent);
                return;
        }
    }

    private static void EmitBody(StringBuilder builder, ParameterBinding parameter, int index, string indent)
    {
        builder.Append(indent).Append(parameter.DeclaredType).Append(" __arg").Append(index).AppendLine(";");
        builder.Append(indent).AppendLine("{");
        string inner = indent + "    ";

        builder.Append(inner).Append("global::Assimalign.Cohesion.Web.Serialization.IHttpContentSerializationFeature? __serializer").Append(index)
            .AppendLine(" = context.Features.Get<global::Assimalign.Cohesion.Web.Serialization.IHttpContentSerializationFeature>();");
        builder.Append(inner).Append("global::Assimalign.Cohesion.Http.HttpMediaType.TryParse(context.Request.Headers.GetValue(global::Assimalign.Cohesion.Http.HttpHeaderKey.ContentType), out var __mediaType")
            .Append(index).AppendLine(");");
        builder.Append(inner).Append("if (__serializer").Append(index).Append(" is null || __serializer").Append(index)
            .Append(".GetReader(__mediaType").Append(index).AppendLine(") is null)");
        EmitUnsupportedMediaType(builder, inner);
        builder.Append(inner).AppendLine("try");
        builder.Append(inner).AppendLine("{");
        builder.Append(inner).Append("    __arg").Append(index).Append(" = (await context.Request.ReadContentAsync<")
            .Append(parameter.DeclaredType).AppendLine(">(context.RequestCancelled))!;");
        builder.Append(inner).AppendLine("}");
        builder.Append(inner).AppendLine("catch (global::System.Text.Json.JsonException)");
        EmitBadRequest(builder, inner, "$body", "The request body could not be deserialized.");
        builder.Append(inner).AppendLine("catch (global::Assimalign.Cohesion.Web.Serialization.HttpContentSerializationException)");
        EmitUnsupportedMediaType(builder, inner);
        builder.Append(indent).AppendLine("}");
    }

    private static void EmitRoute(StringBuilder builder, ParameterBinding parameter, int index, string indent)
    {
        builder.Append(indent).Append(parameter.DeclaredType).Append(" __arg").Append(index).AppendLine(";");
        builder.Append(indent).Append("object? __raw").Append(index).AppendLine(" = null;");
        builder.Append(indent).Append("if (context.TryGetRouteValues(out var __routeValues").Append(index).Append(") && __routeValues")
            .Append(index).Append(" is not null) { __routeValues").Append(index).Append(".TryGetValue(\"").Append(parameter.Key)
            .Append("\", out __raw").Append(index).AppendLine("); }");

        string raw = "__raw" + index;
        string arg = "__arg" + index;

        switch (parameter.Conversion)
        {
            case ConversionKind.String:
                if (parameter.Required)
                {
                    builder.Append(indent).Append("if (").Append(raw).AppendLine(" is null)");
                    EmitBadRequest(builder, indent, parameter.Key, "The route value is required.");
                }

                builder.Append(indent).Append(arg).Append(" = ").Append(raw).Append(" as string ?? ").Append(raw).AppendLine("?.ToString();");
                return;

            case ConversionKind.Parsable:
            case ConversionKind.Enum:
                builder.Append(indent).Append("if (").Append(raw).Append(" is ").Append(parameter.CoreType).Append(" __typed").Append(index)
                    .Append(") { ").Append(arg).Append(" = __typed").Append(index).AppendLine("; }");
                builder.Append(indent).Append("else if (").Append(raw).Append(" is not null && ")
                    .Append(ParseExpression(parameter.Conversion, parameter.CoreType, raw + ".ToString()", arg)).AppendLine(") { }");
                builder.Append(indent).AppendLine("else");
                EmitBadRequest(builder, indent, parameter.Key, "The route value could not be parsed.");
                return;

            case ConversionKind.NullableParsable:
            case ConversionKind.NullableEnum:
                builder.Append(indent).Append("if (").Append(raw).Append(" is null) { ").Append(arg).AppendLine(" = null; }");
                builder.Append(indent).Append("else if (").Append(raw).Append(" is ").Append(parameter.CoreType).Append(" __typed").Append(index)
                    .Append(") { ").Append(arg).Append(" = __typed").Append(index).AppendLine("; }");
                builder.Append(indent).Append("else if (")
                    .Append(ParseExpression(parameter.Conversion, parameter.CoreType, raw + ".ToString()", "var __parsed" + index))
                    .Append(") { ").Append(arg).Append(" = __parsed").Append(index).AppendLine("; }");
                builder.Append(indent).AppendLine("else");
                EmitBadRequest(builder, indent, parameter.Key, "The route value could not be parsed.");
                return;
        }
    }

    private static void EmitScalar(StringBuilder builder, ParameterBinding parameter, int index, string indent)
    {
        builder.Append(indent).Append(parameter.DeclaredType).Append(" __arg").Append(index).AppendLine(";");

        string raw = "__raw" + index;
        builder.Append(indent).Append("string? ").Append(raw).Append(" = ").Append(ReadScalarSource(parameter, index)).AppendLine(";");

        string arg = "__arg" + index;

        switch (parameter.Conversion)
        {
            case ConversionKind.String:
                if (parameter.Required)
                {
                    builder.Append(indent).Append("if (").Append(raw).AppendLine(" is null)");
                    EmitBadRequest(builder, indent, parameter.Key, "The value is required.");
                }

                builder.Append(indent).Append(arg).Append(" = ").Append(raw).AppendLine(";");
                return;

            case ConversionKind.Parsable:
            case ConversionKind.Enum:
                builder.Append(indent).Append("if (").Append(raw).Append(" is null || !")
                    .Append(ParseExpression(parameter.Conversion, parameter.CoreType, raw, arg)).AppendLine(")");
                EmitBadRequest(builder, indent, parameter.Key, "The value could not be parsed.");
                return;

            case ConversionKind.NullableParsable:
            case ConversionKind.NullableEnum:
                builder.Append(indent).Append("if (string.IsNullOrEmpty(").Append(raw).Append(")) { ").Append(arg).AppendLine(" = null; }");
                builder.Append(indent).Append("else if (")
                    .Append(ParseExpression(parameter.Conversion, parameter.CoreType, raw, "var __parsed" + index))
                    .Append(") { ").Append(arg).Append(" = __parsed").Append(index).AppendLine("; }");
                builder.Append(indent).AppendLine("else");
                EmitBadRequest(builder, indent, parameter.Key, "The value could not be parsed.");
                return;
        }
    }

    private static string ReadScalarSource(ParameterBinding parameter, int index) => parameter.Source switch
    {
        BindingSource.Query => "context.Request.Query.TryGetValue(\"" + parameter.Key + "\", out var __query" + index + ") ? __query" + index + ".Value : null",
        BindingSource.Header => "context.Request.Headers.GetValue(\"" + parameter.Key + "\")",
        BindingSource.Form => "__form.TryGetValue(\"" + parameter.Key + "\", out var __field" + index + ") ? __field" + index + ".Value : null",
        _ => "null"
    };

    private static string ParseExpression(ConversionKind conversion, string coreType, string valueExpression, string target)
    {
        bool isEnum = conversion == ConversionKind.Enum || conversion == ConversionKind.NullableEnum;

        return isEnum
            ? "global::System.Enum.TryParse<" + coreType + ">(" + valueExpression + ", true, out " + target + ")"
            : coreType + ".TryParse(" + valueExpression + ", global::System.Globalization.CultureInfo.InvariantCulture, out " + target + ")";
    }

    private static void EmitBadRequest(StringBuilder builder, string indent, string key, string detail)
    {
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    global::Assimalign.Cohesion.Web.ProblemDetails __problem = global::Assimalign.Cohesion.Web.ProblemDetails.FromStatus(global::Assimalign.Cohesion.Http.HttpStatusCode.BadRequest, \"One or more binding errors occurred.\");");
        builder.Append(indent).Append("    __problem.Extensions[\"errors\"] = new global::System.Collections.Generic.Dictionary<string, object?> { [\"")
            .Append(key).Append("\"] = new string[] { \"").Append(detail).AppendLine("\" } };");
        builder.Append(indent).AppendLine("    await context.Response.WriteProblemDetailsAsync(__problem, context.RequestCancelled);");
        builder.Append(indent).AppendLine("    return;");
        builder.Append(indent).AppendLine("}");
    }

    private static void EmitUnsupportedMediaType(StringBuilder builder, string indent)
    {
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    global::Assimalign.Cohesion.Web.ProblemDetails __problem = global::Assimalign.Cohesion.Web.ProblemDetails.FromStatus(global::Assimalign.Cohesion.Http.HttpStatusCode.UnsupportedMediaType, \"The request Content-Type is not supported.\");");
        builder.Append(indent).AppendLine("    await context.Response.WriteProblemDetailsAsync(__problem, context.RequestCancelled);");
        builder.Append(indent).AppendLine("    return;");
        builder.Append(indent).AppendLine("}");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static bool ImplementsParsable(ITypeSymbol type, INamedTypeSymbol? parsableType)
    {
        if (parsableType is null)
        {
            return false;
        }

        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, parsableType)
                && candidate.TypeArguments.Length == 1
                && SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsInterface(ITypeSymbol type, INamedTypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(type, target))
        {
            return true;
        }

        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, target))
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectRouteTokens(string pattern, HashSet<string> tokens)
    {
        int index = 0;

        while (index < pattern.Length)
        {
            int open = pattern.IndexOf('{', index);
            if (open < 0)
            {
                break;
            }

            int close = pattern.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }

            string token = pattern.Substring(open + 1, close - open - 1).Trim();

            int colon = token.IndexOf(':');
            if (colon >= 0)
            {
                token = token.Substring(0, colon);
            }

            int equals = token.IndexOf('=');
            if (equals >= 0)
            {
                token = token.Substring(0, equals);
            }

            token = token.TrimStart('*').Trim();

            if (token.Length > 0)
            {
                tokens.Add(token);
            }

            index = close + 1;
        }
    }

    private static string VerbToMethod(string verb) => verb switch
    {
        "MapGet" => "Get",
        "MapPost" => "Post",
        "MapPut" => "Put",
        "MapPatch" => "Patch",
        "MapDelete" => "Delete",
        _ => "Get"
    };
}
