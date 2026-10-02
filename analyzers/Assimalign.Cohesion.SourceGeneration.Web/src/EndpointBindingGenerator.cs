using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

using Assimalign.Cohesion.SourceGeneration.Web.Internal;

namespace Assimalign.Cohesion.SourceGeneration.Web;

/// <summary>
/// Emits AOT-safe binding thunks for the typed <c>Map*</c> endpoint overloads on
/// <c>Assimalign.Cohesion.Web.WebApplicationPipelineBuilderExtensions</c> (the application) and
/// <c>Assimalign.Cohesion.Web.RouterGroupBuilderEndpointExtensions</c> (route groups). Each typed call site — for
/// example <c>app.MapGet("/users/{id}", (int id, IHttpContext context) =&gt; ...)</c> — is intercepted
/// with a C# interceptor that casts the handler back to its concrete delegate type, binds each
/// parameter from the request (route / query / header / body / form, plus direct injections), invokes
/// the handler directly, and writes the value it returns, if any. No reflection and no expression
/// compilation happen at run time. A call site whose handler cannot be modeled is reported as a
/// <c>COHWEB</c> compile-time error instead of being left to the placeholder overload, which throws.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EndpointBindingGenerator : IIncrementalGenerator
{
    private const string WebNamespace = "Assimalign.Cohesion.Web";
    private const string GeneratedNamespace = "Assimalign.Cohesion.Web.Api.Generated";

    // The antiforgery requirement a form-bound endpoint carries. It lives in Web.Antiforgery, which the
    // generator does not reference: the requirement is emitted only when the consuming compilation can
    // name it, so an application without the package is unaffected.
    private const string antiforgeryMetadataTypeName = "Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata";
    private const string antiforgeryRequirement = ".WithMetadata(global::" + antiforgeryMetadataTypeName + ".Required)";

    // The Web.Serialization entry points the emitted code calls to read a body and to write a returned
    // value. The generator does not reference the package either; a call site that needs one reports
    // COHWEB0007 when the consuming compilation cannot name it.
    private const string requestSerializationTypeName = "Assimalign.Cohesion.Web.Serialization.HttpRequestSerializationExtensions";
    private const string contentNegotiationTypeName = "Assimalign.Cohesion.Web.Serialization.HttpContentNegotiationExtensions";

    // Generated code invokes a handler through its Func<...>/Action<...> type, which takes at most 16 parameters.
    private const int maxHandlerParameters = 16;

    private static readonly SymbolDisplayFormat _fullyQualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // The same, without nullable reference annotations: the form typeof(...) accepts, for the endpoint
    // description metadata.
    private static readonly SymbolDisplayFormat _typeOf = _fullyQualified.RemoveMiscellaneousOptions(
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly HashSet<string> _verbs = new()
    {
        "Map", "MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete"
    };

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<EndpointAnalysis> analyses = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidate(node),
                transform: static (ctx, ct) => Analyze(ctx, ct))
            .Where(static analysis => analysis is not null)
            .Select(static (analysis, _) => analysis!.Value);

        // Diagnostics go through RegisterSourceOutput so the IDE reports them while the code is typed; the
        // interceptors themselves are implementation-only output.
        context.RegisterSourceOutput(
            analyses
                .Where(static analysis => analysis.Diagnostics.Count > 0)
                .Select(static (analysis, _) => analysis.Diagnostics),
            static (spc, diagnostics) => Report(spc, diagnostics));

        IncrementalValueProvider<ImmutableArray<EndpointBinding>> endpoints = analyses
            .Where(static analysis => analysis.Binding is not null)
            .Select(static (analysis, _) => analysis.Binding!.Value)
            .Collect();

        context.RegisterImplementationSourceOutput(endpoints, static (spc, models) => Emit(spc, models));
    }

    private static bool IsCandidate(SyntaxNode node)
        => node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax memberAccess,
            ArgumentList.Arguments.Count: >= 2
        }
        && _verbs.Contains(memberAccess.Name.Identifier.Text);

    // ---------------------------------------------------------------------
    // Analysis
    // ---------------------------------------------------------------------

    private static EndpointAnalysis? Analyze(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        SemanticModel model = ctx.SemanticModel;
        Compilation compilation = model.Compilation;

        // The operation tree identifies the handler, not the symbol API: a method group converted to
        // System.Delegate reports no symbol (only a candidate), while its operation carries the delegate
        // creation, the target method, and the delegate type the compiler inferred.
        if (model.GetOperation(invocation, ct) is not IInvocationOperation operation)
        {
            return null;
        }

        IMethodSymbol method = operation.TargetMethod;

        if (!_verbs.Contains(method.Name))
        {
            return null;
        }

        // The extension symbol should be the Web.Api mapping helper.
        if (method.ContainingNamespace?.ToDisplayString() is { } containingNamespace
            && !containingNamespace.StartsWith(WebNamespace, StringComparison.Ordinal))
        {
            return null;
        }

        // Identify the typed (System.Delegate) overload by the parameters its arguments bind to, so named
        // arguments in any order resolve; the WebApplicationMiddleware overloads are registered verbatim
        // and are not our concern.
        INamedTypeSymbol? httpMethodType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.Http.HttpMethod");
        IArgumentOperation? handlerArgument = null;
        IArgumentOperation? patternArgument = null;
        bool hasMethodParameter = false;

        foreach (IArgumentOperation argument in operation.Arguments)
        {
            if (argument.Parameter is not { } parameter)
            {
                continue;
            }

            if (parameter.Type.ToDisplayString() == "System.Delegate")
            {
                handlerArgument = argument;
            }
            else if (parameter.Type.SpecialType == SpecialType.System_String)
            {
                patternArgument = argument;
            }
            else if (httpMethodType is not null && SymbolEqualityComparer.Default.Equals(parameter.Type, httpMethodType))
            {
                hasMethodParameter = true;
            }
        }

        if (handlerArgument?.Syntax is not ArgumentSyntax handlerSyntax
            || patternArgument?.Syntax is not ArgumentSyntax patternSyntax)
        {
            return null;
        }

        // Route tokens from a literal pattern power name-based route inference; the pattern also names the
        // endpoint in diagnostics.
        HashSet<string> routeTokens = new(StringComparer.OrdinalIgnoreCase);
        bool literalPattern = false;
        string endpoint = method.Name;

        if (patternSyntax.Expression is LiteralExpressionSyntax { Token.Value: string patternText } patternLiteral)
        {
            CollectRouteTokens(patternText, routeTokens);
            literalPattern = true;
            endpoint = method.Name + "(" + patternLiteral.Token.Text + ")";
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax receiverAccess
            || model.GetTypeInfo(receiverAccess.Expression, ct).Type is not ITypeSymbol receiverType)
        {
            return null;
        }

        // The visible template is the whole template only for a literal pattern mapped on the
        // application. A route-group endpoint composes a prefix declared elsewhere, so a parameter its
        // own template does not name may still be a route parameter: bind it from the route values,
        // falling back to the query string.
        INamedTypeSymbol? groupType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.Web.Routing.IRouterGroupBuilder");
        bool partialTemplate = !literalPattern
            || (groupType is not null
                && (SymbolEqualityComparer.Default.Equals(receiverType, groupType) || ImplementsInterface(receiverType, groupType)));

        // The handler: a lambda or a method group the compiler turned into a delegate.
        ExpressionSyntax handlerExpression = handlerSyntax.Expression;
        Location handlerLocation = GetHandlerLocation(handlerExpression);

        IOperation handlerValue = handlerArgument.Value;
        while (handlerValue is IConversionOperation conversion)
        {
            handlerValue = conversion.Operand;
        }

        if (handlerValue is not IDelegateCreationOperation creation)
        {
            // A value the compiler cannot type is already an error of its own.
            if (handlerValue is IInvalidOperation || handlerValue.Type is null || handlerValue.Type.TypeKind == TypeKind.Error)
            {
                return null;
            }

            return Fail(DiagnosticInfo.Create(EndpointBindingDiagnostics.HandlerNotLambdaOrMethodGroup, handlerLocation, endpoint));
        }

        IMethodSymbol? handler = creation.Target switch
        {
            IAnonymousFunctionOperation lambda => lambda.Symbol,
            IMethodReferenceOperation reference => reference.Method,
            _ => null
        };

        if (handler is null || creation.Type is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke } delegateType)
        {
            return null;
        }

        // An extension method group is bound to its receiver: its first parameter is not the handler's.
        ImmutableArray<IParameterSymbol> handlerParameters = handler.Parameters;
        if (handler.IsExtensionMethod && handlerParameters.Length == invoke.Parameters.Length + 1)
        {
            handlerParameters = handlerParameters.RemoveAt(0);
        }

        if (handlerParameters.Length != invoke.Parameters.Length)
        {
            return null;
        }

        // A handler whose signature needs default values, a params array, by-ref parameters or more than
        // sixteen parameters gets a compiler-generated delegate type that generated code cannot name.
        bool anonymousDelegate = delegateType.IsAnonymousType;
        ImmutableArray<DiagnosticInfo>.Builder diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        // Return shape: nothing, an awaited Task/ValueTask, or a value written as the response.
        if (!TryAnalyzeReturn(handler, invoke, compilation, out ReturnKind returnKind, out ITypeSymbol? resultType, out string? returnProblem))
        {
            return null; // an unresolved type: the compiler reports it
        }

        if (returnProblem is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                EndpointBindingDiagnostics.UnsupportedReturnType,
                handlerLocation,
                endpoint,
                invoke.ReturnType.ToDisplayString(HandlerTypeRules.MessageFormat),
                returnProblem));
        }

        INamedTypeSymbol? parsableType = compilation.GetTypeByMetadataName("System.IParsable`1");
        INamedTypeSymbol? contextType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.Http.IHttpContext");
        INamedTypeSymbol? cancellationType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        INamedTypeSymbol? featureType = compilation.GetTypeByMetadataName("Assimalign.Cohesion.Http.IHttpFeature");

        var parameters = ImmutableArray.CreateBuilder<ParameterBinding>(handlerParameters.Length);
        int bodyParameterIndex = -1;
        int formParameterIndex = -1;

        for (int i = 0; i < handlerParameters.Length; i++)
        {
            IParameterSymbol parameter = handlerParameters[i];
            ITypeSymbol parameterType = invoke.Parameters[i].Type;
            Location parameterLocation = GetParameterLocation(parameter, handlerExpression, handlerLocation);

            string? problem = DescribeSignatureProblem(parameter, anonymousDelegate);
            if (problem is null)
            {
                problem = HandlerTypeRules.DescribeParameterTypeProblem(parameterType, compilation, out bool isErrorType);
                if (isErrorType)
                {
                    return null; // an unresolved type: the compiler reports it
                }
            }

            ParameterBinding binding = default;
            if (problem is null)
            {
                problem = Classify(
                    parameter,
                    parameterType,
                    routeTokens,
                    partialTemplate,
                    parsableType,
                    contextType,
                    cancellationType,
                    featureType,
                    out binding);
            }

            if (problem is not null)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    EndpointBindingDiagnostics.UnsupportedParameter,
                    parameterLocation,
                    parameter.Name,
                    endpoint,
                    problem));
                continue;
            }

            if (binding.Source == BindingSource.Body)
            {
                if (bodyParameterIndex >= 0)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        EndpointBindingDiagnostics.MultipleBodyParameters,
                        parameterLocation,
                        endpoint,
                        handlerParameters[bodyParameterIndex].Name,
                        parameter.Name));
                    continue;
                }

                bodyParameterIndex = i;
            }
            else if (binding.Source == BindingSource.Form && formParameterIndex < 0)
            {
                formParameterIndex = i;
            }

            parameters.Add(binding);
        }

        // Body and form both read the one request body.
        if (bodyParameterIndex >= 0 && formParameterIndex >= 0)
        {
            IParameterSymbol later = handlerParameters[Math.Max(bodyParameterIndex, formParameterIndex)];
            diagnostics.Add(DiagnosticInfo.Create(
                EndpointBindingDiagnostics.BodyAndFormParameters,
                GetParameterLocation(later, handlerExpression, handlerLocation),
                endpoint,
                handlerParameters[bodyParameterIndex].Name,
                handlerParameters[formParameterIndex].Name));
        }

        if (anonymousDelegate)
        {
            if (handlerParameters.Length > maxHandlerParameters)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    EndpointBindingDiagnostics.UnnameableDelegateType,
                    handlerLocation,
                    endpoint,
                    $"it declares {handlerParameters.Length} parameters, but generated code invokes a handler as Func<...> or Action<...>, which take at most {maxHandlerParameters}; group request values into a [FromBody] model"));
            }
            else if (diagnostics.Count == 0)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    EndpointBindingDiagnostics.UnnameableDelegateType,
                    handlerLocation,
                    endpoint,
                    "the compiler gives it an anonymous delegate type, which generated code cannot name; give it parameters and a return type that fit Func<...> or Action<...>"));
            }
        }
        else if (diagnostics.Count == 0)
        {
            string? delegateProblem = HandlerTypeRules.DescribeDelegateTypeProblem(delegateType, compilation, out bool isErrorType);
            if (isErrorType)
            {
                return null;
            }

            if (delegateProblem is not null)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    EndpointBindingDiagnostics.UnnameableDelegateType,
                    handlerLocation,
                    endpoint,
                    delegateProblem));
            }
        }

        if (diagnostics.Count > 0)
        {
            return Fail(diagnostics);
        }

        // How the returned value is written: text for a string, the negotiated serializer for anything else.
        ResponseKind response = ResponseKind.None;
        ResultNullCheck nullCheck = ResultNullCheck.None;
        string resultTypeName = string.Empty;
        string writtenTypeName = string.Empty;
        string describedResultTypeName = string.Empty;
        bool describesNoContent = false;

        if (resultType is not null)
        {
            ITypeSymbol writtenType = resultType;

            if (resultType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                nullCheck = ResultNullCheck.NullableValue;
                writtenType = nullable.TypeArguments[0];
                describesNoContent = true;
            }
            else if (resultType.IsReferenceType)
            {
                nullCheck = ResultNullCheck.Reference;
                writtenType = resultType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

                // Every reference result is null-checked at run time, but the description lists the 204
                // only when the compiler's nullability analysis says the result may be null.
                describesNoContent = ResultMayBeNull(resultType, handler, creation.Target, model, compilation, ct);
            }

            response = writtenType.SpecialType == SpecialType.System_String ? ResponseKind.Text : ResponseKind.Serialized;
            resultTypeName = resultType.ToDisplayString(_fullyQualified);
            writtenTypeName = writtenType.ToDisplayString(_fullyQualified);
            describedResultTypeName = writtenType.ToDisplayString(_typeOf);
        }

        // Reading a body and writing a negotiated value both need the content-serialization registry.
        if (bodyParameterIndex >= 0 && !CanName(compilation, requestSerializationTypeName))
        {
            IParameterSymbol body = handlerParameters[bodyParameterIndex];
            diagnostics.Add(DiagnosticInfo.Create(
                EndpointBindingDiagnostics.SerializationNotReferenced,
                GetParameterLocation(body, handlerExpression, handlerLocation),
                endpoint,
                $"binds '{body.Name}' from the request body"));
        }

        if (response == ResponseKind.Serialized && !CanName(compilation, contentNegotiationTypeName))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                EndpointBindingDiagnostics.SerializationNotReferenced,
                handlerLocation,
                endpoint,
                $"returns '{invoke.ReturnType.ToDisplayString(HandlerTypeRules.MessageFormat)}', which is written through content negotiation"));
        }

        if (diagnostics.Count > 0)
        {
            return Fail(diagnostics);
        }

        // A form post is the request a cross-site page can forge, so a form-bound endpoint requires
        // antiforgery validation whenever the application can express the requirement.
        bool usesForm = formParameterIndex >= 0;
        bool requiresAntiforgery = usesForm && CanRequireAntiforgery(compilation);

        InterceptableLocation? location = model.GetInterceptableLocation(invocation, ct);
        if (location is null)
        {
            return null;
        }

        string methodExpression = hasMethodParameter
            ? "method"
            : "global::Assimalign.Cohesion.Http.HttpMethod." + VerbToMethod(method.Name);

        var endpointBinding = new EndpointBinding(
            location.GetInterceptsLocationAttributeSyntax(),
            receiverType.ToDisplayString(_fullyQualified),
            hasMethodParameter,
            methodExpression,
            delegateType.ToDisplayString(_fullyQualified),
            returnKind,
            resultTypeName,
            response,
            writtenTypeName,
            nullCheck,
            describedResultTypeName,
            describesNoContent,
            new EquatableArray<ParameterBinding>(parameters.ToImmutable()),
            bodyParameterIndex,
            usesForm,
            requiresAntiforgery);

        return new EndpointAnalysis(endpointBinding, EquatableArray<DiagnosticInfo>.Empty);
    }

    private static EndpointAnalysis Fail(DiagnosticInfo diagnostic)
        => new(null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(diagnostic)));

    private static EndpointAnalysis Fail(ImmutableArray<DiagnosticInfo>.Builder diagnostics)
        => new(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));

    // True when the consuming compilation can name the type: it resolves and is accessible.
    private static bool CanName(Compilation compilation, string metadataName)
        => compilation.GetTypeByMetadataName(metadataName) is INamedTypeSymbol type
        && compilation.IsSymbolAccessibleWithin(type, compilation.Assembly);

    // True when the consuming compilation references Web.Antiforgery: its metadata type resolves, is
    // accessible, and exposes the static Required instance the emitted code attaches. Without the package
    // the generated route carries no requirement, so the application does not need UseAntiforgery.
    private static bool CanRequireAntiforgery(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName(antiforgeryMetadataTypeName) is not INamedTypeSymbol metadataType
            || !compilation.IsSymbolAccessibleWithin(metadataType, compilation.Assembly))
        {
            return false;
        }

        foreach (ISymbol member in metadataType.GetMembers("Required"))
        {
            if (member is IPropertySymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public })
            {
                return true;
            }
        }

        return false;
    }

    // Classifies the handler's return: false only when a type does not resolve (the compiler reports it).
    // A supported shape sets the kind and, for a value, the result type; an unsupported one sets the
    // reason a COHWEB0002 diagnostic embeds.
    private static bool TryAnalyzeReturn(
        IMethodSymbol handler,
        IMethodSymbol invoke,
        Compilation compilation,
        out ReturnKind kind,
        out ITypeSymbol? resultType,
        out string? problem)
    {
        kind = ReturnKind.Void;
        resultType = null;
        problem = null;

        ITypeSymbol returnType = invoke.ReturnType;

        if (invoke.ReturnsByRef || invoke.ReturnsByRefReadonly)
        {
            problem = "it returns by reference; return the value itself";
            return true;
        }

        if (returnType.SpecialType == SpecialType.System_Void)
        {
            if (handler.IsAsync)
            {
                problem = "an async void handler is not awaited, so the response would complete before the handler finishes and an exception it throws would crash the process; declare it async Task";
            }

            return true;
        }

        if (returnType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        INamedTypeSymbol? task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        INamedTypeSymbol? valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        INamedTypeSymbol? taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        INamedTypeSymbol? valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");

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

        ITypeSymbol value = returnType;
        kind = ReturnKind.Value;

        if (returnType is INamedTypeSymbol { IsGenericType: true } named)
        {
            if (taskOfT is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, taskOfT))
            {
                kind = ReturnKind.TaskOfValue;
                value = named.TypeArguments[0];
            }
            else if (valueTaskOfT is not null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, valueTaskOfT))
            {
                kind = ReturnKind.ValueTaskOfValue;
                value = named.TypeArguments[0];
            }
        }

        problem = HandlerTypeRules.DescribeResultProblem(value, compilation, isAwaitedValue: kind != ReturnKind.Value, out bool isErrorType);
        if (isErrorType)
        {
            return false;
        }

        resultType = value;
        return true;
    }

    // Whether a reference result may be null, for the 204 in the endpoint description. A declared return —
    // a method group's, or a lambda's explicit return type — carries its annotation. An implicitly typed
    // lambda's inferred return type carries none (it is nullable-oblivious even where nullable is
    // enabled), so the null-state of the values the lambda returns decides. Oblivious code lists no 204.
    private static bool ResultMayBeNull(
        ITypeSymbol resultType,
        IMethodSymbol handler,
        IOperation target,
        SemanticModel model,
        Compilation compilation,
        CancellationToken ct)
    {
        if (resultType.NullableAnnotation == NullableAnnotation.Annotated
            || GetAwaitedResult(handler.ReturnType, compilation).NullableAnnotation == NullableAnnotation.Annotated)
        {
            return true;
        }

        if (target is not IAnonymousFunctionOperation lambda)
        {
            return false;
        }

        foreach (IReturnOperation returned in lambda.Body.Descendants().OfType<IReturnOperation>())
        {
            if (returned.ReturnedValue is { } value
                && ReturnsFrom(returned, lambda)
                && model.GetTypeInfo(value.Syntax, ct).Nullability.FlowState == NullableFlowState.MaybeNull)
            {
                return true;
            }
        }

        return false;
    }

    // True when a return statement belongs to the lambda itself, not to a lambda or local function nested in it.
    private static bool ReturnsFrom(IOperation returned, IAnonymousFunctionOperation lambda)
    {
        for (IOperation? current = returned.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, lambda))
            {
                return true;
            }

            if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return false;
            }
        }

        return false;
    }

    // The value a return type produces: the T of Task<T> or ValueTask<T>, otherwise the type itself.
    private static ITypeSymbol GetAwaitedResult(ITypeSymbol returnType, Compilation compilation)
    {
        if (returnType is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            INamedTypeSymbol? taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            INamedTypeSymbol? valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");

            if (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, taskOfT)
                || SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, valueTaskOfT))
            {
                return named.TypeArguments[0];
            }
        }

        return returnType;
    }

    // Parameter modifiers an endpoint cannot honor. Default values and params arrays only matter when
    // they forced a compiler-generated delegate type; on an explicitly created Func they are inert.
    private static string? DescribeSignatureProblem(IParameterSymbol parameter, bool anonymousDelegate)
    {
        if (parameter.RefKind != RefKind.None)
        {
            return "it is passed by reference (ref, out or in), but endpoint parameters are bound by value; remove the modifier";
        }

        if (!anonymousDelegate)
        {
            return null;
        }

        if (parameter.IsParams)
        {
            return "it is a params parameter, which gives the handler a compiler-generated delegate type that generated code cannot name; remove the params modifier";
        }

        if (parameter.HasExplicitDefaultValue)
        {
            return "it declares a default value, which gives the handler a compiler-generated delegate type that generated code cannot name; make it nullable (for example int? page) and apply the default inside the handler";
        }

        return null;
    }

    // Classifies one handler parameter: where it binds from and how its value converts. Returns the
    // reason a COHWEB0003 diagnostic embeds when the parameter cannot be bound, or null when it can.
    private static string? Classify(
        IParameterSymbol parameter,
        ITypeSymbol type,
        HashSet<string> routeTokens,
        bool partialTemplate,
        INamedTypeSymbol? parsableType,
        INamedTypeSymbol? contextType,
        INamedTypeSymbol? cancellationType,
        INamedTypeSymbol? featureType,
        out ParameterBinding binding)
    {
        binding = default;
        string declaredType = type.ToDisplayString(_fullyQualified);
        string describedType = type.ToDisplayString(_typeOf);

        // Direct injections take precedence over any binding source.
        if (contextType is not null && SymbolEqualityComparer.Default.Equals(type, contextType))
        {
            binding = new ParameterBinding(declaredType, "", "", BindingSource.Context, ConversionKind.Injection, "", false, describedType);
            return null;
        }

        if (cancellationType is not null && SymbolEqualityComparer.Default.Equals(type, cancellationType))
        {
            binding = new ParameterBinding(declaredType, "", "", BindingSource.Cancellation, ConversionKind.Injection, "", false, describedType);
            return null;
        }

        if (featureType is not null && ImplementsInterface(type, featureType))
        {
            binding = new ParameterBinding(declaredType, "", declaredType, BindingSource.Feature, ConversionKind.Injection, "", false, describedType);
            return null;
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
        else if (partialTemplate)
        {
            source = BindingSource.RouteOrQuery;
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
            // e.g. [FromQuery] on a complex type
            return $"'{type.ToDisplayString(HandlerTypeRules.MessageFormat)}' cannot be read from a single {DescribeSource(source)} value: route values, query strings, headers and form fields bind string, IParsable<T> types, enums and their nullable forms; bind it from the request body with [FromBody], or change its type";
        }

        binding = new ParameterBinding(declaredType, coreType, "", source, conversion, key, required, describedType);
        return null;
    }

    private static string DescribeSource(BindingSource source) => source switch
    {
        BindingSource.Route => "route",
        BindingSource.Query => "query string",
        BindingSource.Header => "header",
        BindingSource.Form => "form field",
        _ => "route or query string"
    };

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
            string innerType = inner.ToDisplayString(_fullyQualified);

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
            return (ConversionKind.Enum, type.ToDisplayString(_fullyQualified), true);
        }

        if (ImplementsParsable(type, parsableType))
        {
            return (ConversionKind.Parsable, type.ToDisplayString(_fullyQualified), true);
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

    // A lambda's diagnostics point at its head (parameters and arrow) rather than its whole body; any
    // other handler expression is reported whole.
    private static Location GetHandlerLocation(ExpressionSyntax handler)
    {
        if (handler is LambdaExpressionSyntax lambda)
        {
            return Location.Create(lambda.SyntaxTree, TextSpan.FromBounds(lambda.SpanStart, lambda.ArrowToken.Span.End));
        }

        return handler.GetLocation();
    }

    // A lambda's parameter is reported where it is declared. A method group's parameters are declared
    // away from the call site (another file, or metadata), so those are reported at the handler argument.
    private static Location GetParameterLocation(IParameterSymbol parameter, ExpressionSyntax handler, Location fallback)
    {
        foreach (Location location in parameter.Locations)
        {
            if (location.IsInSource
                && location.SourceTree == handler.SyntaxTree
                && handler.Span.Contains(location.SourceSpan))
            {
                return location;
            }
        }

        return fallback;
    }

    // ---------------------------------------------------------------------
    // Emit
    // ---------------------------------------------------------------------

    private static void Report(SourceProductionContext context, EquatableArray<DiagnosticInfo> diagnostics)
    {
        foreach (DiagnosticInfo info in diagnostics)
        {
            context.ReportDiagnostic(info.ToDiagnostic(EndpointBindingDiagnostics.GetDescriptor(info.DescriptorId)));
        }
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<EndpointBinding> models)
    {
        if (models.IsDefaultOrEmpty)
        {
            return;
        }

        bool usesSerialization = models.Any(static model => model.BodyParameterIndex >= 0 || model.Response == ResponseKind.Serialized);

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
        if (usesSerialization)
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
        builder.Append("        public static global::Assimalign.Cohesion.Web.Routing.IRouterRouteBuilder Intercept_")
            .Append(index)
            .Append("(this ")
            .Append(model.ReceiverType)
            .Append(" builder, ");

        if (model.HasMethodParameter)
        {
            builder.Append("global::Assimalign.Cohesion.Http.HttpMethod method, ");
        }

        builder.Append("string pattern, global::System.Delegate handler");

        builder.AppendLine(")");
        builder.AppendLine("        {");
        builder.Append("            var __handler = (").Append(model.DelegateType).AppendLine(")handler;");
        builder.Append("            return builder.Map(")
            .Append(model.MethodExpression)
            .AppendLine(", pattern, async (global::Assimalign.Cohesion.Http.IHttpContext context) =>");
        builder.AppendLine("            {");

        EmitThunkBody(builder, model, "                ");

        builder.AppendLine("            })");

        EmitDescription(builder, model);

        if (model.RequiresAntiforgery)
        {
            // Route-level metadata, attached where the route is mapped: the caller's own chain
            // (.DisableAntiforgery()) comes after it and still wins under last-wins resolution.
            builder.AppendLine();
            builder.Append("            ").Append(antiforgeryRequirement);
        }

        builder.AppendLine(";");

        builder.AppendLine("        }");
    }

    // Describes the endpoint for documentation adapters (#152): one EndpointParameterMetadata per
    // request-bound parameter, in handler order, then the responses — 200 with the written type (none for
    // a handler that writes its own response), and 204 when the result may be null (ResultMayBeNull).
    // Only typeof(...) values are emitted, so the description needs no reflection.
    private static void EmitDescription(StringBuilder builder, EndpointBinding model)
    {
        const string indent = "                ";
        var items = new List<string>();

        foreach (ParameterBinding parameter in model.Parameters)
        {
            if (GetDescribedSource(parameter.Source) is not { } source)
            {
                continue; // injected, not supplied by the request
            }

            // A request body is always required: an empty one fails deserialization with 400.
            bool required = parameter.Source == BindingSource.Body || parameter.Required;

            items.Add("new global::Assimalign.Cohesion.Web.EndpointParameterMetadata("
                + Literal(parameter.Key)
                + ", global::Assimalign.Cohesion.Web.EndpointParameterSource." + source
                + ", typeof(" + parameter.DescribedType + "), "
                + (required ? "true" : "false") + ")");
        }

        string responseType = model.Response == ResponseKind.None ? "null" : "typeof(" + model.DescribedResultType + ")";
        string responseContentType = model.Response == ResponseKind.Text ? "global::Assimalign.Cohesion.Http.HttpMediaType.TextPlain" : "null";

        items.Add("new global::Assimalign.Cohesion.Web.EndpointResponseMetadata(global::Assimalign.Cohesion.Http.HttpStatusCode.Ok, "
            + responseType + ", " + responseContentType + ")");

        if (model.DescribesNoContent)
        {
            items.Add("new global::Assimalign.Cohesion.Web.EndpointResponseMetadata(global::Assimalign.Cohesion.Http.HttpStatusCode.NoContent, null, null)");
        }

        builder.AppendLine("            .WithMetadata(");

        for (int i = 0; i < items.Count; i++)
        {
            builder.Append(indent).Append(items[i]);

            if (i < items.Count - 1)
            {
                builder.AppendLine(",");
            }
        }

        builder.Append(")");
    }

    private static string? GetDescribedSource(BindingSource source) => source switch
    {
        BindingSource.Route => "Route",
        BindingSource.RouteOrQuery => "RouteOrQuery",
        BindingSource.Query => "Query",
        BindingSource.Header => "Header",
        BindingSource.Form => "Form",
        BindingSource.Body => "Body",
        _ => null
    };

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

        string arguments = string.Join(", ", Enumerable.Range(0, parameters.Count).Select(static i => "__arg" + i));
        string invocation = "__handler(" + arguments + ")";

        switch (model.Return)
        {
            case ReturnKind.Void:
                builder.Append(indent).Append(invocation).AppendLine(";");
                return;

            case ReturnKind.Task:
            case ReturnKind.ValueTask:
                builder.Append(indent).Append("await ").Append(invocation).AppendLine(";");
                return;

            case ReturnKind.Value:
                builder.Append(indent).Append(model.ResultType).Append(" __result = ").Append(invocation).AppendLine(";");
                break;

            default:
                builder.Append(indent).Append(model.ResultType).Append(" __result = await ").Append(invocation).AppendLine(";");
                break;
        }

        EmitResponse(builder, model, indent);
    }

    // Writes the value the handler returned. The status is left as the handler (or the response default,
    // 200) set it, except that a null value — the absence of a representation — answers 204 No Content
    // when the handler kept the default, with no body and no serializer consulted.
    private static void EmitResponse(StringBuilder builder, EndpointBinding model, string indent)
    {
        if (model.NullCheck != ResultNullCheck.None)
        {
            builder.Append(indent).AppendLine("if (__result is null)");
            builder.Append(indent).AppendLine("{");
            builder.Append(indent).AppendLine("    if (context.Response.StatusCode.Equals(global::Assimalign.Cohesion.Http.HttpStatusCode.Ok))");
            builder.Append(indent).AppendLine("    {");
            builder.Append(indent).AppendLine("        context.Response.StatusCode = global::Assimalign.Cohesion.Http.HttpStatusCode.NoContent;");
            builder.Append(indent).AppendLine("    }");
            builder.Append(indent).AppendLine();
            builder.Append(indent).AppendLine("    return;");
            builder.Append(indent).AppendLine("}");
        }

        string value = model.NullCheck == ResultNullCheck.NullableValue ? "__result.Value" : "__result";

        if (model.Response == ResponseKind.Text)
        {
            // A string is text, not a serialized JSON string: written as UTF-8 under the handler's own
            // Content-Type when it set one, text/plain otherwise, without negotiation.
            builder.Append(indent).AppendLine("if (!context.Response.Headers.TryGetValue(global::Assimalign.Cohesion.Http.HttpHeaderKey.ContentType, out global::Assimalign.Cohesion.Http.HttpHeaderValue __contentType) || __contentType.IsEmpty)");
            builder.Append(indent).AppendLine("{");
            builder.Append(indent).AppendLine("    context.Response.Headers[global::Assimalign.Cohesion.Http.HttpHeaderKey.ContentType] = \"text/plain; charset=utf-8\";");
            builder.Append(indent).AppendLine("}");
            builder.Append(indent).Append("await context.Response.Body.WriteAsync(global::System.Text.Encoding.UTF8.GetBytes(")
                .Append(value).AppendLine("), context.RequestCancelled);");
            return;
        }

        // Any other value goes through the content-serialization registry with Accept negotiation
        // (RFC 9110 §12.5.1): the negotiated writer serializes it, or nothing is acceptable and the
        // response becomes a bodyless 406.
        builder.Append(indent).Append("await context.WriteNegotiatedContentAsync<").Append(model.WrittenType).Append(">(")
            .Append(value).AppendLine(", context.RequestCancelled);");
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
                EmitRoute(builder, parameter, index, indent, fallbackToQuery: false);
                return;

            case BindingSource.RouteOrQuery:
                EmitRoute(builder, parameter, index, indent, fallbackToQuery: true);
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

    private static void EmitRoute(StringBuilder builder, ParameterBinding parameter, int index, string indent, bool fallbackToQuery)
    {
        string key = Literal(parameter.Key);

        builder.Append(indent).Append(parameter.DeclaredType).Append(" __arg").Append(index).AppendLine(";");
        builder.Append(indent).Append("object? __raw").Append(index).AppendLine(" = null;");
        builder.Append(indent).Append("if (context.TryGetRouteValues(out var __routeValues").Append(index).Append(") && __routeValues")
            .Append(index).Append(" is not null) { __routeValues").Append(index).Append(".TryGetValue(").Append(key)
            .Append(", out __raw").Append(index).AppendLine("); }");

        if (fallbackToQuery)
        {
            // The visible template does not name the parameter, so a route value comes from a group
            // prefix when the route has one; otherwise the query string supplies it.
            builder.Append(indent).Append("if (__raw").Append(index).Append(" is null && context.Request.Query.TryGetValue(")
                .Append(key).Append(", out var __query").Append(index).Append(")) { __raw").Append(index)
                .Append(" = __query").Append(index).AppendLine(".Value; }");
        }

        string raw = "__raw" + index;
        string arg = "__arg" + index;
        string required = fallbackToQuery ? "The value is required." : "The route value is required.";
        string unparsable = fallbackToQuery ? "The value could not be parsed." : "The route value could not be parsed.";

        switch (parameter.Conversion)
        {
            case ConversionKind.String:
                if (parameter.Required)
                {
                    builder.Append(indent).Append("if (").Append(raw).AppendLine(" is null)");
                    EmitBadRequest(builder, indent, parameter.Key, required);
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
                EmitBadRequest(builder, indent, parameter.Key, unparsable);
                return;

            case ConversionKind.NullableParsable:
            case ConversionKind.NullableEnum:
                // An empty query value reads as absent, as it does for a plain query parameter.
                builder.Append(indent).Append("if (").Append(raw)
                    .Append(fallbackToQuery ? " is null or string { Length: 0 }) { " : " is null) { ")
                    .Append(arg).AppendLine(" = null; }");
                builder.Append(indent).Append("else if (").Append(raw).Append(" is ").Append(parameter.CoreType).Append(" __typed").Append(index)
                    .Append(") { ").Append(arg).Append(" = __typed").Append(index).AppendLine("; }");
                builder.Append(indent).Append("else if (")
                    .Append(ParseExpression(parameter.Conversion, parameter.CoreType, raw + ".ToString()", "var __parsed" + index))
                    .Append(") { ").Append(arg).Append(" = __parsed").Append(index).AppendLine("; }");
                builder.Append(indent).AppendLine("else");
                EmitBadRequest(builder, indent, parameter.Key, unparsable);
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
        BindingSource.Query => "context.Request.Query.TryGetValue(" + Literal(parameter.Key) + ", out var __query" + index + ") ? __query" + index + ".Value : null",
        BindingSource.Header => "context.Request.Headers.GetValue(" + Literal(parameter.Key) + ")",
        BindingSource.Form => "__form.TryGetValue(" + Literal(parameter.Key) + ", out var __field" + index + ") ? __field" + index + ".Value : null",
        _ => "null"
    };

    // A binding key reaches generated code only as a C# string literal. Keys come from attribute Name
    // values, which may hold any character, so every one is escaped rather than spliced between quotes.
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

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
        builder.Append(indent).Append("    __problem.Extensions[\"errors\"] = new global::System.Collections.Generic.Dictionary<string, object?> { [")
            .Append(Literal(key)).Append("] = new string[] { ").Append(Literal(detail)).AppendLine(" } };");
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
