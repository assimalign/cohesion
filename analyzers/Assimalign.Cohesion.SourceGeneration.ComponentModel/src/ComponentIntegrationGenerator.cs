using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.SourceGeneration.ComponentModel;

/// <summary>
/// Projects assembly-declared component factories onto composition seams that are present in the
/// consuming compilation.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentIntegrationGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Assimalign.Cohesion.ComponentIntegrationAttribute";
    private const string CoreAssemblyName = "Assimalign.Cohesion.Core";
    private const int CSharp14LanguageVersion = (int)LanguageVersion.CSharp14;

    /// <summary>
    /// Joins the parameter names captured in a <see cref="ProjectionModel"/>. U+001F (unit
    /// separator) cannot occur in a C# identifier, so the join round-trips losslessly.
    /// </summary>
    private const string ParameterNameSeparator = "\u001f";

    private static readonly SymbolDisplayFormat TypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Included)
        .WithMiscellaneousOptions(
            (SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                & ~SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<CollectedProjections> projections = context.CompilationProvider
            .Select(static (compilation, cancellationToken) => Collect(compilation, cancellationToken));
        IncrementalValueProvider<int> language = context.ParseOptionsProvider
            .Select(static (options, _) => options is CSharpParseOptions csharp
                ? (int)csharp.LanguageVersion
                : 0);

        context.RegisterSourceOutput(
            projections.Combine(language),
            static (productionContext, pair) => Emit(productionContext, pair.Left, pair.Right));
    }

    private static CollectedProjections Collect(Compilation compilation, CancellationToken cancellationToken)
    {
        if (compilation.GetTypesByMetadataName(AttributeMetadataName).IsDefaultOrEmpty)
        {
            return CollectedProjections.Empty;
        }

        var projections = new List<ProjectionModel>();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var missingSeams = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        CollectFromAssembly(
            compilation,
            compilation.Assembly,
            isReferencedAssembly: false,
            projections,
            diagnostics,
            missingSeams,
            cancellationToken);

        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectFromAssembly(
                compilation,
                assembly,
                isReferencedAssembly: true,
                projections,
                diagnostics,
                missingSeams,
                cancellationToken);
        }

        foreach (KeyValuePair<string, List<string>> missing in missingSeams.OrderBy(
                     static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            missing.Value.Sort(StringComparer.Ordinal);
            string targetTypeName = missing.Value[0];
            string message = $"{missing.Key} offers {missing.Value.Count} additional verb(s); "
                + $"reference the assembly declaring {targetTypeName} to enable them.";
            diagnostics.Add(DiagnosticInfo.Create(
                ComponentIntegrationDiagnostics.MissingSeam,
                Location.None,
                message));
        }

        List<ProjectionModel> ordered = projections
            .OrderBy(static projection => projection.DeclaringAssemblyName, StringComparer.Ordinal)
            .ThenBy(static projection => projection.Verb, StringComparer.Ordinal)
            .ThenBy(static projection => projection.ParameterList, StringComparer.Ordinal)
            .ThenBy(static projection => projection.SeamMetadataName, StringComparer.Ordinal)
            .ThenBy(static projection => projection.FactoryTypeName, StringComparer.Ordinal)
            .ThenBy(static projection => projection.FactoryMethodName, StringComparer.Ordinal)
            .ToList();

        var unique = ImmutableArray.CreateBuilder<ProjectionModel>();
        var keys = new HashSet<ProjectionKey>();
        foreach (ProjectionModel projection in ordered)
        {
            var key = new ProjectionKey(
                projection.SeamMetadataName,
                projection.Verb,
                projection.ParameterList);
            if (keys.Add(key))
            {
                unique.Add(projection);
                continue;
            }

            diagnostics.Add(DiagnosticInfo.Create(
                ComponentIntegrationDiagnostics.DuplicateProjection,
                Location.None,
                $"The projection '{projection.Verb}({projection.ParameterList})' from "
                    + $"'{projection.DeclaringAssemblyName}' duplicates an earlier projection for "
                    + $"'{projection.SeamMetadataName}' and has been ignored."));
        }

        return new CollectedProjections(
            new EquatableArray<ProjectionModel>(unique.ToImmutable()),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    private static void CollectFromAssembly(
        Compilation compilation,
        IAssemblySymbol assembly,
        bool isReferencedAssembly,
        List<ProjectionModel> projections,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        Dictionary<string, List<string>> missingSeams,
        CancellationToken cancellationToken)
    {
        if (isReferencedAssembly && !CouldContainDeclaration(assembly))
        {
            return;
        }

        string assemblyName = assembly.Identity.Name;
        foreach (AttributeData attribute in assembly.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(
                    attribute.AttributeClass?.ToDisplayString(),
                    AttributeMetadataName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryDecode(
                    attribute,
                    out string targetTypeName,
                    out string targetMethodName,
                    out INamedTypeSymbol factoryType,
                    out string factoryMethodName,
                    out string? declaredVerb,
                    out INamedTypeSymbol? contract,
                    out string error))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    ComponentIntegrationDiagnostics.MalformedDeclaration,
                    Location.None,
                    $"The [ComponentIntegration] declaration in '{assemblyName}' is malformed or "
                        + $"unresolvable: {error}."));
                continue;
            }

            ImmutableArray<INamedTypeSymbol> seams = compilation.GetTypesByMetadataName(targetTypeName);
            if (seams.IsDefaultOrEmpty)
            {
                if (!missingSeams.TryGetValue(assemblyName, out List<string>? targets))
                {
                    targets = new List<string>();
                    missingSeams.Add(assemblyName, targets);
                }
                targets.Add(targetTypeName);
                continue;
            }

            INamedTypeSymbol seam = seams[0];
            string rawVerb = declaredVerb ?? factoryMethodName;
            if (!TryRenderIdentifier(targetMethodName, out string renderedTargetMethodName)
                || !TryRenderIdentifier(factoryMethodName, out string renderedFactoryMethodName)
                || !TryRenderIdentifier(rawVerb, out string renderedVerb))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    ComponentIntegrationDiagnostics.MalformedDeclaration,
                    Location.None,
                    $"The [ComponentIntegration] declaration in '{assemblyName}' contains a member "
                        + "name that is not a valid C# identifier."));
                continue;
            }

            string integrationName = $"{assemblyName}:{factoryType.ToDisplayString(TypeDisplayFormat)}.{factoryMethodName}";

            if (!SymbolEqualityComparer.Default.Equals(factoryType.ContainingAssembly, assembly))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    ComponentIntegrationDiagnostics.InaccessibleFactory,
                    Location.None,
                    $"Component integration '{integrationName}' requires its factory type to be "
                        + "declared in the contributing assembly."));
                continue;
            }

            if (!factoryType.IsStatic || !IsExternallyVisible(factoryType))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    ComponentIntegrationDiagnostics.InaccessibleFactory,
                    Location.None,
                    $"Component integration '{integrationName}' requires a public static factory type."));
                continue;
            }

            IMethodSymbol[] methods = factoryType.GetMembers(factoryMethodName)
                .OfType<IMethodSymbol>()
                .Where(static method => method.IsStatic
                    && method.DeclaredAccessibility == Accessibility.Public
                    && method.MethodKind == MethodKind.Ordinary)
                .ToArray();

            if (methods.Length == 0)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    ComponentIntegrationDiagnostics.InaccessibleFactory,
                    Location.None,
                    $"Component integration '{integrationName}' has no public static ordinary factory method."));
                continue;
            }

            foreach (IMethodSymbol method in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (method.IsGenericMethod)
                {
                    AddUnsupportedShapeDiagnostic(diagnostics, integrationName, "generic factory methods are not supported");
                    continue;
                }

                if (method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
                {
                    AddUnsupportedShapeDiagnostic(diagnostics, integrationName, "ref, out, and in parameters are not supported");
                    continue;
                }

                if (method.ReturnsVoid)
                {
                    AddUnsupportedShapeDiagnostic(diagnostics, integrationName, "factory methods must return a value");
                    continue;
                }

                if (!IsExternallyVisible(method.ReturnType))
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        ComponentIntegrationDiagnostics.InaccessibleFactory,
                        Location.None,
                        $"Component integration '{integrationName}' has a return type that is not externally visible."));
                    continue;
                }

                IParameterSymbol? inaccessibleParameter = method.Parameters.FirstOrDefault(
                    static parameter => !IsExternallyVisible(parameter.Type));
                if (inaccessibleParameter is not null)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        ComponentIntegrationDiagnostics.InaccessibleFactory,
                        Location.None,
                        $"Component integration '{integrationName}' has parameter "
                            + $"'{inaccessibleParameter.Name}' whose type is not externally visible."));
                    continue;
                }

                bool returnsFunc = TryGetFuncProduct(method.ReturnType, out ITypeSymbol productType);
                if (IsDisposable(productType) && !returnsFunc)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        ComponentIntegrationDiagnostics.DisposableInstance,
                        Location.None,
                        $"Component integration '{integrationName}' directly returns disposable type "
                            + $"'{productType.ToDisplayString(TypeDisplayFormat)}'; return a System.Func<...> "
                            + "so the container captures the product for disposal."));
                }

                projections.Add(new ProjectionModel(
                    assemblyName,
                    factoryType.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : factoryType.ContainingNamespace.ToDisplayString(),
                    targetTypeName,
                    seam.ToDisplayString(TypeDisplayFormat),
                    seam.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : seam.ContainingNamespace.ToDisplayString(),
                    seam.ContainingAssembly.Identity.Name,
                    renderedTargetMethodName,
                    factoryType.ToDisplayString(TypeDisplayFormat),
                    renderedFactoryMethodName,
                    renderedVerb,
                    RenderParameterList(method.Parameters),
                    RenderArgumentList(method.Parameters),
                    contract?.ToDisplayString(TypeDisplayFormat) ?? string.Empty,
                    contract is not null,
                    string.Join(
                        ParameterNameSeparator,
                        method.Parameters.Select(static parameter => parameter.Name))));
            }
        }
    }

    private static bool CouldContainDeclaration(IAssemblySymbol assembly)
    {
        if (string.Equals(assembly.Identity.Name, CoreAssemblyName, StringComparison.Ordinal))
        {
            return true;
        }

        IModuleSymbol? module = assembly.Modules.FirstOrDefault();
        return module is not null && module.ReferencedAssemblies.Any(
            static identity => identity.Name == CoreAssemblyName);
    }

    private static bool TryDecode(
        AttributeData attribute,
        out string targetTypeName,
        out string targetMethodName,
        out INamedTypeSymbol factoryType,
        out string factoryMethodName,
        out string? verb,
        out INamedTypeSymbol? contract,
        out string error)
    {
        targetTypeName = string.Empty;
        targetMethodName = string.Empty;
        factoryType = null!;
        factoryMethodName = string.Empty;
        verb = null;
        contract = null;
        error = string.Empty;

        if (attribute.ConstructorArguments.Length != 4 || !HasExpectedConstructorShape(attribute))
        {
            error = "a constructor with shape (string, string, Type, string) is required";
            return false;
        }

        TypedConstant targetType = attribute.ConstructorArguments[0];
        TypedConstant targetMethod = attribute.ConstructorArguments[1];
        TypedConstant factory = attribute.ConstructorArguments[2];
        TypedConstant factoryMethod = attribute.ConstructorArguments[3];

        if (HasError(targetType)
            || HasError(targetMethod)
            || HasError(factory)
            || HasError(factoryMethod))
        {
            error = "a constructor argument contains an error symbol";
            return false;
        }

        if (!IsStringConstant(targetType, allowNull: false)
            || targetType.Value is not string decodedTargetType
            || decodedTargetType.Length == 0
            || !IsStringConstant(targetMethod, allowNull: false)
            || targetMethod.Value is not string decodedTargetMethod
            || decodedTargetMethod.Length == 0
            || !IsTypeConstant(factory, allowNull: false)
            || factory.Value is not INamedTypeSymbol decodedFactory
            || decodedFactory.TypeKind == TypeKind.Error
            || !IsStringConstant(factoryMethod, allowNull: false)
            || factoryMethod.Value is not string decodedFactoryMethod
            || decodedFactoryMethod.Length == 0)
        {
            error = "constructor arguments do not match (string, string, Type, string)";
            return false;
        }

        targetTypeName = decodedTargetType;
        targetMethodName = decodedTargetMethod;
        factoryType = decodedFactory;
        factoryMethodName = decodedFactoryMethod;

        foreach (KeyValuePair<string, TypedConstant> namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "Verb")
            {
                if (HasError(namedArgument.Value)
                    || !HasExpectedNamedPropertyShape(attribute, "Verb", expectsType: false)
                    || !IsStringConstant(namedArgument.Value, allowNull: true))
                {
                    error = "Verb must be a string or null";
                    return false;
                }

                verb = namedArgument.Value.Value as string;
                if (verb is not null && verb.Length == 0)
                {
                    error = "Verb cannot be empty";
                    return false;
                }
            }
            else if (namedArgument.Key == "Contract")
            {
                if (HasError(namedArgument.Value)
                    || !HasExpectedNamedPropertyShape(attribute, "Contract", expectsType: true)
                    || !IsTypeConstant(namedArgument.Value, allowNull: true))
                {
                    error = "Contract must be a Type or null";
                    return false;
                }

                if (namedArgument.Value.Value is null)
                {
                    contract = null;
                }
                else if (namedArgument.Value.Value is INamedTypeSymbol decodedContract
                    && decodedContract.TypeKind != TypeKind.Error)
                {
                    contract = decodedContract;
                }
                else
                {
                    error = "Contract must be a Type or null";
                    return false;
                }
            }
            else
            {
                error = $"'{namedArgument.Key}' is not a supported named argument";
                return false;
            }
        }

        return true;
    }

    private static bool HasExpectedConstructorShape(AttributeData attribute)
    {
        if (attribute.AttributeConstructor is not IMethodSymbol constructor
            || constructor.Parameters.Length != 4)
        {
            return false;
        }

        return constructor.Parameters[0].Type.SpecialType == SpecialType.System_String
            && constructor.Parameters[1].Type.SpecialType == SpecialType.System_String
            && IsSystemType(constructor.Parameters[2].Type)
            && constructor.Parameters[3].Type.SpecialType == SpecialType.System_String;
    }

    private static bool HasExpectedNamedPropertyShape(
        AttributeData attribute,
        string propertyName,
        bool expectsType)
    {
        IPropertySymbol? property = attribute.AttributeClass?.GetMembers(propertyName)
            .OfType<IPropertySymbol>()
            .SingleOrDefault();
        return property is not null
            && (expectsType
                ? IsSystemType(property.Type)
                : property.Type.SpecialType == SpecialType.System_String);
    }

    private static bool IsSystemType(ITypeSymbol type) =>
        type is INamedTypeSymbol namedType
        && namedType.MetadataName == "Type"
        && namedType.ContainingType is null
        && namedType.ContainingNamespace.ToDisplayString() == "System";

    private static bool IsStringConstant(TypedConstant constant, bool allowNull) =>
        constant.Kind == TypedConstantKind.Primitive
        && constant.Type?.SpecialType == SpecialType.System_String
        && (constant.Value is string || (allowNull && constant.IsNull));

    private static bool IsTypeConstant(TypedConstant constant, bool allowNull) =>
        constant.Kind == TypedConstantKind.Type
        && constant.Type is not null
        && IsSystemType(constant.Type)
        && (constant.Value is ITypeSymbol || (allowNull && constant.IsNull));

    private static bool HasError(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Error
            || (constant.Type is not null && ContainsErrorSymbol(constant.Type)))
        {
            return true;
        }

        if (constant.Kind == TypedConstantKind.Array)
        {
            return constant.Values.Any(static value => HasError(value));
        }

        return constant.Value is ITypeSymbol type && ContainsErrorSymbol(type);
    }

    private static bool ContainsErrorSymbol(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Error)
        {
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            return ContainsErrorSymbol(array.ElementType);
        }

        if (type is IPointerTypeSymbol pointer)
        {
            return ContainsErrorSymbol(pointer.PointedAtType);
        }

        if (type is IFunctionPointerTypeSymbol functionPointer)
        {
            return ContainsErrorSymbol(functionPointer.Signature.ReturnType)
                || functionPointer.Signature.Parameters.Any(
                    static parameter => ContainsErrorSymbol(parameter.Type));
        }

        return type is INamedTypeSymbol namedType
            && ((namedType.ContainingType is not null && ContainsErrorSymbol(namedType.ContainingType))
                || namedType.TypeArguments.Any(static argument => ContainsErrorSymbol(argument)));
    }

    private static bool TryRenderIdentifier(string value, out string rendered)
    {
        if (SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None)
        {
            rendered = "@" + value;
            return true;
        }

        if (SyntaxFacts.IsValidIdentifier(value))
        {
            rendered = value;
            return true;
        }

        rendered = string.Empty;
        return false;
    }

    private static void AddUnsupportedShapeDiagnostic(
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        string integrationName,
        string reason)
    {
        diagnostics.Add(DiagnosticInfo.Create(
            ComponentIntegrationDiagnostics.UnsupportedFactoryShape,
            Location.None,
            $"Component integration '{integrationName}' cannot be projected because {reason}."));
    }

    private static bool IsExternallyVisible(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Error || type.SpecialType == SpecialType.System_Void)
        {
            return false;
        }

        if (type is IArrayTypeSymbol array)
        {
            return IsExternallyVisible(array.ElementType);
        }

        if (type is IPointerTypeSymbol pointer)
        {
            return IsExternallyVisible(pointer.PointedAtType);
        }

        if (type is IFunctionPointerTypeSymbol functionPointer)
        {
            return IsExternallyVisible(functionPointer.Signature.ReturnType)
                && functionPointer.Signature.Parameters.All(
                    static parameter => IsExternallyVisible(parameter.Type));
        }

        if (type.TypeKind == TypeKind.Dynamic)
        {
            return true;
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        for (INamedTypeSymbol? current = namedType; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return namedType.TypeArguments.All(static argument => IsExternallyVisible(argument));
    }

    private static bool TryGetFuncProduct(ITypeSymbol returnType, out ITypeSymbol productType)
    {
        if (returnType is INamedTypeSymbol namedType
            && namedType.Name == "Func"
            && namedType.ContainingNamespace.ToDisplayString() == "System"
            && namedType.TypeArguments.Length >= 1)
        {
            productType = namedType.TypeArguments[namedType.TypeArguments.Length - 1];
            return true;
        }

        productType = returnType;
        return false;
    }

    private static bool IsDisposable(ITypeSymbol type)
    {
        if (IsDisposableInterface(type))
        {
            return true;
        }

        return type is INamedTypeSymbol namedType
            && namedType.AllInterfaces.Any(static candidate => IsDisposableInterface(candidate));
    }

    private static bool IsDisposableInterface(ITypeSymbol type)
    {
        string name = type.ToDisplayString();
        return name == "System.IDisposable" || name == "System.IAsyncDisposable";
    }

    private static string RenderParameterList(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(", ", parameters.Select(static parameter => RenderParameter(parameter)));

    private static string RenderParameter(IParameterSymbol parameter)
    {
        var builder = new StringBuilder();
        if (parameter.IsParams)
        {
            builder.Append("params ");
        }

        builder.Append(parameter.Type.ToDisplayString(TypeDisplayFormat));
        builder.Append(" @");
        builder.Append(parameter.Name);

        if (parameter.HasExplicitDefaultValue)
        {
            builder.Append(" = ");
            builder.Append(RenderDefaultValue(parameter));
        }

        return builder.ToString();
    }

    private static string RenderArgumentList(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(", ", parameters.Select(static parameter => "@" + parameter.Name));

    private static string RenderDefaultValue(IParameterSymbol parameter)
    {
        object? value = parameter.ExplicitDefaultValue;
        if (value is null)
        {
            return parameter.Type.IsReferenceType ? "null" : "default";
        }

        if (parameter.Type.TypeKind == TypeKind.Enum
            && parameter.Type is INamedTypeSymbol enumType
            && enumType.EnumUnderlyingType is INamedTypeSymbol underlyingType)
        {
            return $"({parameter.Type.ToDisplayString(TypeDisplayFormat)})("
                + RenderNumericConstant(value, underlyingType.SpecialType)
                + ")";
        }

        if (value is bool boolean)
        {
            return boolean ? "true" : "false";
        }

        if (value is char character)
        {
            return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(character, quote: true);
        }

        if (value is string text)
        {
            return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, quote: true);
        }

        return value switch
        {
            sbyte => RenderNumericConstant(value, SpecialType.System_SByte),
            byte => RenderNumericConstant(value, SpecialType.System_Byte),
            short => RenderNumericConstant(value, SpecialType.System_Int16),
            ushort => RenderNumericConstant(value, SpecialType.System_UInt16),
            int => RenderNumericConstant(value, SpecialType.System_Int32),
            uint => RenderNumericConstant(value, SpecialType.System_UInt32),
            long => RenderNumericConstant(value, SpecialType.System_Int64),
            ulong => RenderNumericConstant(value, SpecialType.System_UInt64),
            float => RenderNumericConstant(value, SpecialType.System_Single),
            double => RenderNumericConstant(value, SpecialType.System_Double),
            decimal => RenderNumericConstant(value, SpecialType.System_Decimal),
            _ => "default"
        };
    }

    private static string RenderNumericConstant(object value, SpecialType specialType)
    {
        switch (specialType)
        {
            case SpecialType.System_SByte:
                return Convert.ToSByte(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case SpecialType.System_Byte:
                return Convert.ToByte(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case SpecialType.System_Int16:
                return Convert.ToInt16(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case SpecialType.System_UInt16:
                return Convert.ToUInt16(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case SpecialType.System_Int32:
                return Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case SpecialType.System_UInt32:
                return Convert.ToUInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "U";
            case SpecialType.System_Int64:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "L";
            case SpecialType.System_UInt64:
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "UL";
            case SpecialType.System_Single:
                return RenderSingle(Convert.ToSingle(value, CultureInfo.InvariantCulture));
            case SpecialType.System_Double:
                return RenderDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case SpecialType.System_Decimal:
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "M";
            default:
                return "default";
        }
    }

    private static string RenderSingle(float value)
    {
        if (float.IsNaN(value))
        {
            return "global::System.Single.NaN";
        }
        if (float.IsPositiveInfinity(value))
        {
            return "global::System.Single.PositiveInfinity";
        }
        if (float.IsNegativeInfinity(value))
        {
            return "global::System.Single.NegativeInfinity";
        }
        return value.ToString("R", CultureInfo.InvariantCulture) + "F";
    }

    private static string RenderDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return "global::System.Double.NaN";
        }
        if (double.IsPositiveInfinity(value))
        {
            return "global::System.Double.PositiveInfinity";
        }
        if (double.IsNegativeInfinity(value))
        {
            return "global::System.Double.NegativeInfinity";
        }
        return value.ToString("R", CultureInfo.InvariantCulture) + "D";
    }

    private static void Emit(
        SourceProductionContext context,
        CollectedProjections collected,
        int languageVersion)
    {
        foreach (DiagnosticInfo diagnostic in collected.Diagnostics)
        {
            context.ReportDiagnostic(diagnostic.ToDiagnostic(
                ComponentIntegrationDiagnostics.GetDescriptor(diagnostic.DescriptorId)));
        }

        if (collected.Projections.Count == 0)
        {
            return;
        }

        if (languageVersion < CSharp14LanguageVersion)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ComponentIntegrationDiagnostics.UnsupportedLanguageVersion,
                Location.None,
                $"The consuming compilation uses language version value '{languageVersion}'; "
                    + "C# 14 or later is required to emit component integration extension members."));
            return;
        }

        foreach (IGrouping<string, ProjectionModel> assemblyGroup in collected.Projections
                     .GroupBy(static projection => projection.DeclaringAssemblyName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            string source = RenderAssembly(assemblyGroup.Key, assemblyGroup.ToArray());
            context.AddSource(
                $"CohesionComponents.{assemblyGroup.Key}.g.cs",
                SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string RenderAssembly(string assemblyName, ProjectionModel[] projections)
    {
        var builder = new StringBuilder();
        ProjectionModel first = projections[0];
        string receiver = SelectReceiverName(projections);

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("// Projected by Assimalign.Cohesion.SourceGeneration.ComponentModel from");
        builder.AppendLine($"// [assembly: ComponentIntegration] declared in {assemblyName},");
        builder.AppendLine($"// because this compilation also references the assembly declaring {first.SeamTypeName}.");
        builder.AppendLine("// Deviates from the repo file-scoped-namespace rule per design decision: generated files");
        builder.AppendLine("// carry a block namespace, matching MapperProfileGenerator and EndpointBindingGenerator.");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#pragma warning disable CS1591");
        builder.AppendLine();

        foreach (string seamNamespace in projections
                     .Select(static projection => projection.SeamNamespace)
                     .Where(static value => value.Length > 0)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static value => value, StringComparer.Ordinal))
        {
            builder.AppendLine($"using {seamNamespace};");
        }

        builder.AppendLine();

        foreach (IGrouping<string, ProjectionModel> namespaceGroup in projections
                     .GroupBy(static projection => projection.ContributorNamespace, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            AppendNamespace(
                builder,
                namespaceGroup.Key,
                assemblyName,
                receiver,
                namespaceGroup.ToArray());
        }

        return builder.ToString();
    }

    private static string SelectReceiverName(ProjectionModel[] projections)
    {
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProjectionModel projection in projections)
        {
            foreach (string parameterName in projection.ParameterNames.Split(
                         new[] { ParameterNameSeparator[0] },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                parameterNames.Add(parameterName);
            }
        }

        if (!parameterNames.Contains("builder"))
        {
            return "builder";
        }

        if (!parameterNames.Contains("receiver"))
        {
            return "receiver";
        }

        const string fallback = "componentBuilder";
        if (!parameterNames.Contains(fallback))
        {
            return fallback;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = fallback + suffix.ToString(CultureInfo.InvariantCulture);
            if (!parameterNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static void AppendNamespace(
        StringBuilder builder,
        string contributorNamespace,
        string assemblyName,
        string receiver,
        ProjectionModel[] projections)
    {
        bool hasNamespace = contributorNamespace.Length > 0;
        string indent = hasNamespace ? "    " : string.Empty;
        if (hasNamespace)
        {
            builder.AppendLine($"namespace {contributorNamespace}");
            builder.AppendLine("{");
        }

        builder.AppendLine($"{indent}/// <summary>Component integrations projected from <c>{EscapeXml(assemblyName)}</c>.</summary>");
        builder.AppendLine($"{indent}internal static class {assemblyName.Replace('.', '_')}ComponentIntegrations");
        builder.AppendLine($"{indent}{{");

        foreach (IGrouping<string, ProjectionModel> seamGroup in projections
                     .GroupBy(static projection => projection.SeamTypeName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"{indent}    extension({seamGroup.Key} {receiver})");
            builder.AppendLine($"{indent}    {{");

            foreach (ProjectionModel projection in seamGroup
                         .OrderBy(static item => item.Verb, StringComparer.Ordinal)
                         .ThenBy(static item => item.ParameterList, StringComparer.Ordinal))
            {
                string contract = projection.HasContract
                    ? $"<{projection.ContractTypeName}>"
                    : string.Empty;

                builder.AppendLine(
                    $"{indent}        /// <summary>Registers the component produced by "
                    + $"<c>{EscapeXml(projection.FactoryTypeName)}.{EscapeXml(projection.FactoryMethodName)}</c> "
                    + $"on this <c>{EscapeXml(projection.SeamTypeName)}</c>.</summary>");
                builder.AppendLine(
                    $"{indent}        public {projection.SeamTypeName} {projection.Verb}({projection.ParameterList})");
                builder.AppendLine($"{indent}        {{");
                builder.AppendLine(
                    $"{indent}            {receiver}.{projection.TargetMethodName}{contract}("
                    + $"{projection.FactoryTypeName}.{projection.FactoryMethodName}({projection.ArgumentList}));");
                builder.AppendLine($"{indent}            return {receiver};");
                builder.AppendLine($"{indent}        }}");
                builder.AppendLine();
            }

            builder.AppendLine($"{indent}    }}");
        }

        builder.AppendLine($"{indent}}}");
        if (hasNamespace)
        {
            builder.AppendLine("}");
        }
        builder.AppendLine();
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private readonly record struct ProjectionKey(
        string SeamMetadataName,
        string Verb,
        string ParameterList);

    private readonly record struct ProjectionModel(
        string DeclaringAssemblyName,
        string ContributorNamespace,
        string SeamMetadataName,
        string SeamTypeName,
        string SeamNamespace,
        string SeamAssemblyName,
        string TargetMethodName,
        string FactoryTypeName,
        string FactoryMethodName,
        string Verb,
        string ParameterList,
        string ArgumentList,
        string ContractTypeName,
        bool HasContract,
        string ParameterNames);

    private readonly record struct CollectedProjections(
        EquatableArray<ProjectionModel> Projections,
        EquatableArray<DiagnosticInfo> Diagnostics)
    {
        internal static CollectedProjections Empty => new(
            EquatableArray<ProjectionModel>.Empty,
            EquatableArray<DiagnosticInfo>.Empty);
    }
}
