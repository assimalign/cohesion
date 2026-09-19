using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Assimalign.Cohesion.Sdk.Database.Tasks.Compilation;

/// <summary>
/// Reads the deliberately small, analyzable Database schema DSL from consumer C#.
/// It never emits or invokes the consumer assembly.
/// </summary>
internal sealed class CSharpSchemaExtractor(Action<SchemaSourceDiagnostic> report)
{
    private const string SchemaBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.ISqlSchemaBuilder";
    private const string TableBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.ISqlTableBuilder<T>";
    private const string TypeBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.ISqlTypeBuilder";
    private const string PrincipalBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.ISqlPrincipalBuilder";

    private static readonly SymbolDisplayFormat TypeDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private readonly Action<SchemaSourceDiagnostic> _report = report ?? throw new ArgumentNullException(nameof(report));
    private bool _hasErrors;

    public SchemaSourceModel? Extract(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> referencePaths,
        string assemblyName,
        string? languageVersion,
        string? defineConstants)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(referencePaths);

        CSharpParseOptions parseOptions = CreateParseOptions(languageVersion, defineConstants);
        var syntaxTrees = new List<SyntaxTree>();
        foreach (string sourcePath in sourcePaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.Ordinal))
        {
            try
            {
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(
                    File.ReadAllText(sourcePath),
                    parseOptions,
                    sourcePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Error("COHDBSDK100", $"Could not read C# schema source '{sourcePath}': {exception.Message}", location: null, sourcePath);
            }
        }

        var references = new List<MetadataReference>();
        foreach (string referencePath in referencePaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.Ordinal))
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(referencePath));
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException)
            {
                Error("COHDBSDK100", $"Could not read compiler reference '{referencePath}': {exception.Message}", location: null, referencePath);
            }
        }

        if (_hasErrors)
        {
            return null;
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable));

        var calls = new List<(InvocationExpressionSyntax Invocation, IMethodSymbol Method, SemanticModel SemanticModel)>();
        foreach (SyntaxTree syntaxTree in syntaxTrees)
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
            foreach (InvocationExpressionSyntax invocation in syntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                IMethodSymbol? method = ResolveMethod(semanticModel, invocation);
                if (method is not null && IsSchemaDeclaration(method))
                {
                    calls.Add((invocation, method, semanticModel));
                }
            }
        }

        if (calls.Count != 1)
        {
            Location? location = calls.Count > 0 ? calls[0].Invocation.GetLocation() : null;
            Error(
                "COHDBSDK101",
                $"Database schema compilation requires exactly one SqlSchema.Create(name, configure) or SqlSchema.Compile(name, configure) declaration; found {calls.Count}.",
                location);
            return null;
        }

        (InvocationExpressionSyntax schemaDeclaration, IMethodSymbol schemaDeclarationMethod, SemanticModel model) = calls[0];
        ExpressionSyntax? nameExpression = GetArgument(schemaDeclaration, schemaDeclarationMethod, "name", 0);
        string? name = nameExpression is null ? null : ConstantString(model, nameExpression);
        if (string.IsNullOrWhiteSpace(name))
        {
            Error("COHDBSDK102", $"SqlSchema.{schemaDeclarationMethod.Name} name must be a non-empty compile-time string constant.", nameExpression?.GetLocation() ?? schemaDeclaration.GetLocation());
        }

        ExpressionSyntax? configureExpression = GetArgument(schemaDeclaration, schemaDeclarationMethod, "configure", 1);
        LambdaExpressionSyntax? configure = UnwrapLambda(configureExpression);
        if (configure is null)
        {
            Error("COHDBSDK103", $"SqlSchema.{schemaDeclarationMethod.Name} configuration must be an inline lambda so the build can analyze it without executing Program.Main.", configureExpression?.GetLocation() ?? schemaDeclaration.GetLocation());
            return null;
        }

        var types = new List<SchemaTypeSource>();
        var tables = new List<SchemaTableSource>();
        var functions = new List<SchemaFunctionSource>();
        var triggers = new List<SchemaTriggerSource>();
        var principals = new List<SchemaPrincipalSource>();
        var extensions = new List<SchemaExtensionSource>();
        bool allowsDestructiveChanges = false;

        foreach (InvocationExpressionSyntax invocation in configure.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            IMethodSymbol? method = ResolveMethod(model, invocation);
            if (method is null || !IsOnNamedType(method, SchemaBuilderType))
            {
                continue;
            }

            switch (method.Name)
            {
                case "AllowDestructiveChanges":
                    allowsDestructiveChanges = true;
                    break;
                case "Type":
                    types.Add(ExtractType(model, invocation, method));
                    break;
                case "Table":
                    tables.Add(ExtractTable(model, invocation, method));
                    break;
                case "Extension":
                    extensions.Add(ExtractExtension(model, invocation, method));
                    break;
                case "Function":
                    functions.Add(ExtractFunction(model, invocation, method));
                    break;
                case "Trigger":
                    triggers.Add(ExtractTrigger(model, invocation, method));
                    break;
                case "Principal":
                    principals.Add(ExtractPrincipal(model, invocation, method));
                    break;
                default:
                    Error("COHDBSDK104", $"Schema operation '{method.Name}' is not supported by this SDK compiler.", invocation.GetLocation());
                    break;
            }
        }

        ValidateUnique(types.Select(static item => item.TypeName), "type", configure.GetLocation());
        ValidateUnique(tables.Select(static item => item.RowType), "table", configure.GetLocation());
        ValidateUnique(tables.Select(static item => item.Name), "table name", configure.GetLocation());
        ValidateUnique(functions.Select(static item => item.Name), "function", configure.GetLocation());
        ValidateUnique(principals.Select(static item => item.Name), "principal", configure.GetLocation());
        ValidateUnique(extensions.Select(static item => item.Name), "extension", configure.GetLocation());
        ValidateTables(types, tables, configure.GetLocation());
        ValidateTriggers(tables, triggers, configure.GetLocation());
        ValidateGrants(tables, functions, principals, configure.GetLocation());

        if (_hasErrors || name is null)
        {
            return null;
        }

        return new SchemaSourceModel(
            name,
            allowsDestructiveChanges,
            types.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToArray(),
            tables.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray(),
            functions.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray(),
            triggers.OrderBy(static item => item.RowType, StringComparer.Ordinal).ThenBy(static item => item.Event, StringComparer.Ordinal).ToArray(),
            principals.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray(),
            extensions.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray());
    }

    private SchemaTypeSource ExtractType(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        ITypeSymbol type = method.TypeArguments.Single();
        ExpressionSyntax? configureExpression = GetArgument(invocation, method, "configure", 0);
        LambdaExpressionSyntax? configure = UnwrapLambda(configureExpression);
        if (configure is null)
        {
            Error("COHDBSDK103", "Type configuration must be an inline lambda.", configureExpression?.GetLocation() ?? invocation.GetLocation());
            return new SchemaTypeSource(TypeName(type), null, null);
        }

        int? precision = null;
        int? scale = null;
        foreach (InvocationExpressionSyntax operation in configure.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            IMethodSymbol? operationMethod = ResolveMethod(model, operation);
            if (operationMethod is null || !IsOnNamedType(operationMethod, TypeBuilderType))
            {
                continue;
            }

            if (operationMethod.Name != "Decimal" || operation.ArgumentList.Arguments.Count != 2)
            {
                Error("COHDBSDK104", $"Type operation '{operationMethod.Name}' is not supported.", operation.GetLocation());
                continue;
            }

            precision = ConstantInt32(model, operation.ArgumentList.Arguments[0].Expression);
            scale = ConstantInt32(model, operation.ArgumentList.Arguments[1].Expression);
            if (precision is null || scale is null)
            {
                Error("COHDBSDK104", "Decimal precision and scale must be compile-time integer constants.", operation.GetLocation());
            }
            else if (precision < 1 || scale < 0 || scale > precision)
            {
                Error("COHDBSDK106", $"Decimal precision/scale '{precision},{scale}' is invalid.", operation.GetLocation());
            }
        }

        if (precision is null || scale is null)
        {
            Error("COHDBSDK106", $"Custom type '{TypeName(type)}' must declare its storage representation with Decimal(precision, scale).", configure.GetLocation());
        }

        return new SchemaTypeSource(TypeName(type), precision, scale);
    }

    private SchemaTableSource ExtractTable(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        ITypeSymbol rowType = method.TypeArguments.Single();
        ExpressionSyntax? nameExpression = method.Parameters.Length == 2
            ? GetArgument(invocation, method, "name", 0)
            : null;
        string tableName = nameExpression is null ? rowType.Name : ConstantString(model, nameExpression) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tableName))
        {
            Error("COHDBSDK102", "Table name must be a non-empty compile-time string constant.", nameExpression?.GetLocation() ?? invocation.GetLocation());
            tableName = rowType.Name;
        }

        ExpressionSyntax? configureExpression = GetArgument(invocation, method, "configure", method.Parameters.Length - 1);
        LambdaExpressionSyntax? configure = UnwrapLambda(configureExpression);
        if (configure is null)
        {
            Error("COHDBSDK103", "Table configuration must be an inline lambda.", configureExpression?.GetLocation() ?? invocation.GetLocation());
            return new SchemaTableSource(tableName, TypeName(rowType), [], null, [], []);
        }

        var columns = new List<SchemaColumnSource>();
        string? primaryKey = null;
        var indexes = new List<string>();
        var references = new List<SchemaReferenceSource>();
        foreach (InvocationExpressionSyntax operation in configure.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            IMethodSymbol? operationMethod = ResolveMethod(model, operation);
            if (operationMethod is null || !IsOnOriginalGenericType(operationMethod, TableBuilderType))
            {
                continue;
            }

            SchemaColumnSource? selectedColumn = operation.ArgumentList.Arguments.Count == 0
                ? null
                : SelectedColumn(model, operation.ArgumentList.Arguments[0].Expression, rowType);
            if (selectedColumn is null)
            {
                Error("COHDBSDK106", $"Table operation '{operationMethod.Name}' must select a direct member of '{TypeName(rowType)}'.", operation.GetLocation());
                continue;
            }
            if (!columns.Any(column => string.Equals(column.Name, selectedColumn.Name, StringComparison.Ordinal)))
            {
                columns.Add(selectedColumn);
            }

            switch (operationMethod.Name)
            {
                case "Column":
                    break;
                case "Key":
                case "PrimaryKey":
                    if (primaryKey is not null && !string.Equals(primaryKey, selectedColumn.Name, StringComparison.Ordinal))
                    {
                        Error("COHDBSDK105", $"Table '{tableName}' declares more than one primary key.", operation.GetLocation());
                    }
                    primaryKey = selectedColumn.Name;
                    break;
                case "Index":
                    indexes.Add(selectedColumn.Name);
                    break;
                case "References":
                    references.Add(new SchemaReferenceSource(selectedColumn.Name, TypeName(operationMethod.TypeArguments.Single())));
                    break;
                default:
                    Error("COHDBSDK104", $"Table operation '{operationMethod.Name}' is not supported.", operation.GetLocation());
                    break;
            }
        }

        return new SchemaTableSource(
            tableName,
            TypeName(rowType),
            columns,
            primaryKey,
            indexes.OrderBy(static item => item, StringComparer.Ordinal).ToArray(),
            references.OrderBy(static item => item.Member, StringComparer.Ordinal).ThenBy(static item => item.TargetType, StringComparer.Ordinal).ToArray());
    }

    private SchemaExtensionSource ExtractExtension(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        ExpressionSyntax? nameExpression = GetArgument(invocation, method, "name", 0);
        ExpressionSyntax? valueExpression = GetArgument(invocation, method, "value", 1);
        string? name = nameExpression is null ? null : ConstantString(model, nameExpression);
        string? value = valueExpression is null ? null : ConstantString(model, valueExpression);
        if (string.IsNullOrWhiteSpace(name) || value is null)
        {
            Error("COHDBSDK104", "Schema extension name and value must be compile-time string constants, and the name cannot be empty.", invocation.GetLocation());
        }
        return new SchemaExtensionSource(name ?? string.Empty, value ?? string.Empty);
    }

    private SchemaFunctionSource ExtractFunction(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        ExpressionSyntax? nameExpression = GetArgument(invocation, method, "name", 0);
        string? name = nameExpression is null ? null : ConstantString(model, nameExpression);
        if (string.IsNullOrWhiteSpace(name))
        {
            Error("COHDBSDK102", "Function name must be a non-empty compile-time string constant.", nameExpression?.GetLocation() ?? invocation.GetLocation());
            name = string.Empty;
        }

        ExpressionSyntax? bodyExpression = GetArgument(invocation, method, "body", 1);
        LambdaExpressionSyntax? body = UnwrapLambda(bodyExpression);
        if (body is null)
        {
            Error("COHDBSDK103", $"Function '{name}' body must be an inline expression lambda.", bodyExpression?.GetLocation() ?? invocation.GetLocation());
            return new SchemaFunctionSource(name, [], TypeName(method.TypeArguments.Last()), string.Empty);
        }

        ImmutableArray<IParameterSymbol> delegateParameters = DelegateInvoke(method.Parameters.Last().Type)?.Parameters ?? [];
        var parameters = delegateParameters
            .Select((parameter, index) => new SchemaParameterSource($"arg{index}", TypeName(UnderlyingType(parameter.Type))))
            .ToArray();
        ITypeSymbol declaredReturnType = DelegateInvoke(method.Parameters.Last().Type)?.ReturnType ?? method.TypeArguments.Last();
        string returnType = TypeName(UnderlyingType(declaredReturnType));
        return new SchemaFunctionSource(name, parameters, returnType, CanonicalExpression(model, body, name));
    }

    private SchemaTriggerSource ExtractTrigger(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        ExpressionSyntax? eventExpression = GetArgument(invocation, method, "triggerEvent", 0);
        string eventName = EnumMemberName(model, eventExpression) ?? string.Empty;
        if (eventName.Length == 0)
        {
            Error("COHDBSDK104", "Trigger event must be a named SqlTriggerEvent constant.", eventExpression?.GetLocation() ?? invocation.GetLocation());
        }

        ExpressionSyntax? bodyExpression = GetArgument(invocation, method, "body", 1);
        LambdaExpressionSyntax? body = UnwrapLambda(bodyExpression);
        if (body is null)
        {
            Error("COHDBSDK103", "Trigger body must be an inline expression lambda.", bodyExpression?.GetLocation() ?? invocation.GetLocation());
        }

        return new SchemaTriggerSource(
            TypeName(method.TypeArguments.Single()),
            eventName,
            body is null ? string.Empty : CanonicalExpression(model, body, $"{TypeName(method.TypeArguments.Single())}.{eventName}"));
    }

    private SchemaPrincipalSource ExtractPrincipal(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        ExpressionSyntax? nameExpression = GetArgument(invocation, method, "name", 0);
        string? name = nameExpression is null ? null : ConstantString(model, nameExpression);
        if (string.IsNullOrWhiteSpace(name))
        {
            Error("COHDBSDK102", "Principal name must be a non-empty compile-time string constant.", nameExpression?.GetLocation() ?? invocation.GetLocation());
            name = string.Empty;
        }

        ExpressionSyntax? configureExpression = GetArgument(invocation, method, "configure", 1);
        LambdaExpressionSyntax? configure = UnwrapLambda(configureExpression);
        if (configure is null)
        {
            Error("COHDBSDK103", $"Principal '{name}' configuration must be an inline lambda.", configureExpression?.GetLocation() ?? invocation.GetLocation());
            return new SchemaPrincipalSource(name, []);
        }

        var grants = new List<SchemaGrantSource>();
        foreach (InvocationExpressionSyntax operation in configure.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            IMethodSymbol? operationMethod = ResolveMethod(model, operation);
            if (operationMethod is null || !IsOnNamedType(operationMethod, PrincipalBuilderType))
            {
                continue;
            }

            if (operationMethod.Name != "Grant" || operation.ArgumentList.Arguments.Count < 2)
            {
                Error("COHDBSDK104", $"Principal operation '{operationMethod.Name}' is not supported.", operation.GetLocation());
                continue;
            }

            string permission = EnumMemberName(model, operation.ArgumentList.Arguments[0].Expression) ?? string.Empty;
            var objects = new List<string>();
            foreach (ArgumentSyntax argument in operation.ArgumentList.Arguments.Skip(1))
            {
                string? objectName = ConstantString(model, argument.Expression);
                if (string.IsNullOrWhiteSpace(objectName))
                {
                    Error("COHDBSDK104", "Grant object names must be non-empty compile-time string constants.", argument.GetLocation());
                    continue;
                }
                objects.Add(objectName);
            }
            grants.Add(new SchemaGrantSource(permission, objects.OrderBy(static item => item, StringComparer.Ordinal).ToArray()));
        }

        return new SchemaPrincipalSource(
            name,
            grants.OrderBy(static item => item.Permission, StringComparer.Ordinal).ThenBy(static item => string.Join("\0", item.Objects), StringComparer.Ordinal).ToArray());
    }

    private void ValidateTables(
        IReadOnlyList<SchemaTypeSource> types,
        IReadOnlyList<SchemaTableSource> tables,
        Location location)
    {
        var declaredTypes = types.Select(static item => item.TypeName).ToHashSet(StringComparer.Ordinal);
        var declaredTables = tables.Select(static item => item.RowType).ToHashSet(StringComparer.Ordinal);
        foreach (SchemaTableSource table in tables)
        {
            if (table.Columns.Count == 0)
            {
                Error("COHDBSDK106", $"Table '{table.Name}' must declare at least one column through Column, Key, Index, or References.", location);
            }
            ValidateUnique(table.Columns.Select(static item => item.Name), $"column on table '{table.Name}'", location);
            ValidateUnique(table.Indexes, $"index on table '{table.Name}'", location);
            var columns = table.Columns.Select(static item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (table.PrimaryKey is not null && !columns.Contains(table.PrimaryKey))
            {
                Error("COHDBSDK106", $"Primary key '{table.PrimaryKey}' does not exist on table '{table.Name}'.", location);
            }
            foreach (string index in table.Indexes.Where(index => !columns.Contains(index)))
            {
                Error("COHDBSDK106", $"Index member '{index}' does not exist on table '{table.Name}'.", location);
            }
            foreach (SchemaReferenceSource reference in table.References)
            {
                if (!columns.Contains(reference.Member))
                {
                    Error("COHDBSDK106", $"Reference member '{reference.Member}' does not exist on table '{table.Name}'.", location);
                }
                if (!declaredTables.Contains(reference.TargetType))
                {
                    Error("COHDBSDK106", $"Reference from table '{table.Name}' targets undeclared table type '{reference.TargetType}'.", location);
                }
                else
                {
                    SchemaTableSource target = tables.First(candidate =>
                        string.Equals(candidate.RowType, reference.TargetType, StringComparison.Ordinal));
                    if (target.PrimaryKey is null)
                    {
                        Error("COHDBSDK106", $"Referenced table type '{reference.TargetType}' must declare a primary key.", location);
                    }
                    else
                    {
                        SchemaColumnSource sourceColumn = table.Columns.First(column =>
                            string.Equals(column.Name, reference.Member, StringComparison.OrdinalIgnoreCase));
                        SchemaColumnSource targetColumn = target.Columns.First(column =>
                            string.Equals(column.Name, target.PrimaryKey, StringComparison.OrdinalIgnoreCase));
                        if (!string.Equals(sourceColumn.TypeName, targetColumn.TypeName, StringComparison.Ordinal))
                        {
                            Error(
                                "COHDBSDK106",
                                $"Reference column '{table.Name}.{sourceColumn.Name}' type '{sourceColumn.TypeName}' does not match primary key '{target.Name}.{targetColumn.Name}' type '{targetColumn.TypeName}'.",
                                location);
                        }
                    }
                }
            }
            foreach (SchemaColumnSource column in table.Columns)
            {
                if (!IsBuiltInType(column.TypeName) && !declaredTypes.Contains(column.TypeName))
                {
                    Error("COHDBSDK106", $"Column '{table.Name}.{column.Name}' uses unknown schema type '{column.TypeName}'. Declare it with database.Type<T>(...).", location);
                }
            }
        }
    }

    private void ValidateTriggers(
        IReadOnlyList<SchemaTableSource> tables,
        IReadOnlyList<SchemaTriggerSource> triggers,
        Location location)
    {
        var tableTypes = tables.Select(static item => item.RowType).ToHashSet(StringComparer.Ordinal);
        foreach (SchemaTriggerSource trigger in triggers.Where(trigger => !tableTypes.Contains(trigger.RowType)))
        {
            Error("COHDBSDK106", $"Trigger target type '{trigger.RowType}' is not a declared table.", location);
        }
    }

    private void ValidateGrants(
        IReadOnlyList<SchemaTableSource> tables,
        IReadOnlyList<SchemaFunctionSource> functions,
        IReadOnlyList<SchemaPrincipalSource> principals,
        Location location)
    {
        var objects = tables.Select(static item => item.Name)
            .Concat(functions.Select(static item => item.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SchemaPrincipalSource principal in principals)
        {
            foreach (string grantedObject in principal.Grants.SelectMany(static grant => grant.Objects).Where(item => !objects.Contains(item)))
            {
                Error("COHDBSDK106", $"Principal '{principal.Name}' grants access to undeclared schema object '{grantedObject}'.", location);
            }
        }
    }

    private void ValidateUnique(IEnumerable<string> names, string kind, Location location)
    {
        foreach (IGrouping<string, string> duplicate in names.GroupBy(static name => name, StringComparer.OrdinalIgnoreCase).Where(static group => group.Count() > 1))
        {
            Error("COHDBSDK105", $"Database schema declares duplicate {kind} '{duplicate.Key}'.", location);
        }
    }

    private static bool IsNullable(ITypeSymbol type, NullableAnnotation annotation)
    {
        _ = annotation;
        return type.IsReferenceType ||
            type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    }

    private static ITypeSymbol UnderlyingType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
    }

    private static bool IsBuiltInType(string typeName)
    {
        return typeName is
            "System.Private.CoreLib:System.Boolean" or "System.Private.CoreLib:System.Byte" or
            "System.Private.CoreLib:System.SByte" or "System.Private.CoreLib:System.Int16" or
            "System.Private.CoreLib:System.Int32" or "System.Private.CoreLib:System.Int64" or
            "System.Private.CoreLib:System.Single" or "System.Private.CoreLib:System.Double" or
            "System.Private.CoreLib:System.Decimal" or "System.Private.CoreLib:System.String" or
            "System.Private.CoreLib:System.Guid" or "System.Private.CoreLib:System.DateTime" or
            "System.Private.CoreLib:System.DateTimeOffset" or "System.Private.CoreLib:System.DateOnly" or
            "System.Private.CoreLib:System.TimeOnly" or "System.Private.CoreLib:System.TimeSpan" or
            "System.Private.CoreLib:System.Byte[]";
    }

    private static SchemaColumnSource? SelectedColumn(SemanticModel model, ExpressionSyntax selectorExpression, ITypeSymbol rowType)
    {
        LambdaExpressionSyntax? selector = UnwrapLambda(selectorExpression);
        if (selector?.Body is not ExpressionSyntax body)
        {
            return null;
        }

        while (body is ParenthesizedExpressionSyntax parenthesized)
        {
            body = parenthesized.Expression;
        }
        while (body is CastExpressionSyntax cast)
        {
            body = cast.Expression;
        }

        ISymbol? symbol = model.GetSymbolInfo(body).Symbol;
        if (symbol is not (IPropertySymbol or IFieldSymbol) ||
            !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, rowType))
        {
            return null;
        }

        ITypeSymbol type = symbol switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            _ => throw new InvalidOperationException()
        };
        NullableAnnotation annotation = symbol switch
        {
            IPropertySymbol property => property.NullableAnnotation,
            IFieldSymbol field => field.NullableAnnotation,
            _ => NullableAnnotation.None
        };
        return new SchemaColumnSource(symbol.Name, TypeName(UnderlyingType(type)), IsNullable(type, annotation));
    }

    private static IMethodSymbol? DelegateInvoke(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { Name: "Expression", TypeArguments.Length: 1 } expression)
        {
            type = expression.TypeArguments[0];
        }
        return (type as INamedTypeSymbol)?.DelegateInvokeMethod;
    }

    private string CanonicalExpression(SemanticModel model, LambdaExpressionSyntax lambda, string declaration)
    {
        try
        {
            return CSharpExpressionCanonicalizer.Canonicalize(model, lambda);
        }
        catch (NotSupportedException exception)
        {
            Error(
                "COHDBSDK107",
                $"Expression '{declaration}' cannot be compiled as deterministic schema syntax: {exception.Message}",
                lambda.GetLocation());
            return string.Empty;
        }
    }

    private static string? EnumMemberName(SemanticModel model, ExpressionSyntax? expression)
    {
        return expression is not null && model.GetSymbolInfo(expression).Symbol is IFieldSymbol { HasConstantValue: true } field
            ? field.Name
            : null;
    }

    private static string? ConstantString(SemanticModel model, ExpressionSyntax expression)
    {
        Optional<object?> value = model.GetConstantValue(expression);
        return value.HasValue ? value.Value as string : null;
    }

    private static int? ConstantInt32(SemanticModel model, ExpressionSyntax expression)
    {
        Optional<object?> value = model.GetConstantValue(expression);
        return value.HasValue ? Convert.ToInt32(value.Value, CultureInfo.InvariantCulture) : null;
    }

    private static LambdaExpressionSyntax? UnwrapLambda(ExpressionSyntax? expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }
        return expression as LambdaExpressionSyntax;
    }

    private static ExpressionSyntax? GetArgument(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        string parameterName,
        int fallbackIndex)
    {
        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            if (string.Equals(argument.NameColon?.Name.Identifier.ValueText, parameterName, StringComparison.Ordinal))
            {
                return argument.Expression;
            }
        }
        return fallbackIndex < invocation.ArgumentList.Arguments.Count
            ? invocation.ArgumentList.Arguments[fallbackIndex].Expression
            : null;
    }

    private static IMethodSymbol? ResolveMethod(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        SymbolInfo info = model.GetSymbolInfo(invocation);
        return info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static bool IsSchemaDeclaration(IMethodSymbol method)
    {
        if (method.Name is not ("Create" or "Compile") ||
            !IsOnNamedType(method, "Assimalign.Cohesion.Database.Sql.Schema.SqlSchema") ||
            method.Parameters.Length != 2)
        {
            return false;
        }
        IMethodSymbol? configure = DelegateInvoke(method.Parameters[1].Type);
        return configure?.Parameters.Length == 1 &&
            string.Equals(DisplayTypeName(configure.Parameters[0].Type), SchemaBuilderType, StringComparison.Ordinal);
    }

    private static bool IsOnNamedType(IMethodSymbol method, string typeName)
    {
        return string.Equals(DisplayTypeName(method.ContainingType), typeName, StringComparison.Ordinal);
    }

    private static bool IsOnOriginalGenericType(IMethodSymbol method, string typeName)
    {
        INamedTypeSymbol containingType = method.ContainingType.OriginalDefinition;
        return string.Equals(typeName, TableBuilderType, StringComparison.Ordinal) &&
            string.Equals(containingType.MetadataName, "ISqlTableBuilder`1", StringComparison.Ordinal) &&
            string.Equals(containingType.ContainingNamespace.ToDisplayString(), "Assimalign.Cohesion.Database.Sql.Schema", StringComparison.Ordinal);
    }

    private static string TypeName(ITypeSymbol type)
    {
        return CSharpTypeIdentity.Create(type);
    }

    private static string DisplayTypeName(ITypeSymbol type) => type.ToDisplayString(TypeDisplayFormat);

    private CSharpParseOptions CreateParseOptions(string? languageVersion, string? defineConstants)
    {
        LanguageVersion version = LanguageVersion.Preview;
        if (!string.IsNullOrWhiteSpace(languageVersion) &&
            !LanguageVersionFacts.TryParse(languageVersion, out version))
        {
            Error("COHDBSDK100", $"C# language version '{languageVersion}' is not recognized by the Database schema compiler.", location: null);
            version = LanguageVersion.Preview;
        }

        IEnumerable<string> symbols = (defineConstants ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new CSharpParseOptions(version, preprocessorSymbols: symbols);
    }

    private void Error(string code, string message, Location? location, string? fallbackFile = null)
    {
        _hasErrors = true;
        FileLinePositionSpan span = location?.GetLineSpan() ?? default;
        _report(new SchemaSourceDiagnostic(
            code,
            message,
            string.IsNullOrEmpty(span.Path) ? fallbackFile : span.Path,
            span.IsValid ? span.StartLinePosition.Line + 1 : 0,
            span.IsValid ? span.StartLinePosition.Character + 1 : 0));
    }
}
