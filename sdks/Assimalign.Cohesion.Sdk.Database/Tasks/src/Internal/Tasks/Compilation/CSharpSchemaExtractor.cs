using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Assimalign.Cohesion.Sdk.Database.Tasks.Internal;

/// <summary>
/// Reads the deliberately small, analyzable Database schema DSL from consumer C#: one schema
/// declaration per database. It never emits or invokes the consumer assembly.
/// </summary>
/// <remarks>
/// Two anchors declare a database's schema, both recognized by metadata name (the task references
/// only <c>Database.Sql.Schema</c>, so the engine types are names, not symbols it links against):
/// <list type="bullet">
/// <item><c>SqlSchema.Create(name, configure)</c> or <c>SqlSchema.Compile(name, configure)</c>, the
/// database named by the constant first argument;</item>
/// <item><c>SqlDatabaseBuilder.Schema(declare)</c>, the database named by the constant first argument
/// of the <c>SqlDatabaseEngineBuilder.AddDatabase(name, configure)</c> whose callback parameter
/// receives the call.</item>
/// </list>
/// </remarks>
internal sealed class CSharpSchemaExtractor
{
    private const string schemaType = "Assimalign.Cohesion.Database.Sql.Schema.SqlSchema";
    private const string schemaBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.SqlSchemaBuilder";
    private const string tableBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.SqlTableBuilder<TRow>";
    private const string typeBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.SqlTypeBuilder";
    private const string principalBuilderType = "Assimalign.Cohesion.Database.Sql.Schema.SqlPrincipalBuilder";
    private const string databaseBuilderType = "Assimalign.Cohesion.Database.Sql.SqlDatabaseBuilder";
    private const string engineBuilderType = "Assimalign.Cohesion.Database.Sql.SqlDatabaseEngineBuilder";

    // Characters no artifact file name may carry on any build host. The engine stores a database
    // under a directory of its name, so a name that fails here cannot name a database on disk either.
    private static readonly char[] _invalidFileNameCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly SymbolDisplayFormat _typeDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private readonly Action<SchemaSourceDiagnostic> _report;
    private bool _hasErrors;

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpSchemaExtractor"/> class.
    /// </summary>
    /// <param name="report">The callback that receives each schema source diagnostic.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    public CSharpSchemaExtractor(Action<SchemaSourceDiagnostic> report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>
    /// Extracts every database's schema declaration from the consumer sources.
    /// </summary>
    /// <param name="sourcePaths">The consumer C# source files.</param>
    /// <param name="referencePaths">The compiler reference assemblies.</param>
    /// <param name="assemblyName">The consumer assembly's simple name.</param>
    /// <param name="languageVersion">The consumer's C# language version.</param>
    /// <param name="defineConstants">The consumer's preprocessor constants.</param>
    /// <returns>One model per declared database in ordinal name order, or null after a reported error.</returns>
    public IReadOnlyList<SchemaSourceModel>? Extract(
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

        var declarations = new List<SchemaDeclaration>();
        foreach (SyntaxTree syntaxTree in syntaxTrees)
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
            foreach (InvocationExpressionSyntax invocation in syntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                IMethodSymbol? method = ResolveMethod(semanticModel, invocation);
                if (method is null)
                {
                    continue;
                }

                if (IsSchemaFactory(method))
                {
                    declarations.Add(new SchemaDeclaration(invocation, method, semanticModel, IsDatabaseBuilderSchema: false));
                }
                else if (IsDatabaseBuilderSchema(method))
                {
                    declarations.Add(new SchemaDeclaration(invocation, method, semanticModel, IsDatabaseBuilderSchema: true));
                }
            }
        }

        if (declarations.Count == 0)
        {
            Error(
                "COHDBSDK101",
                "Database schema compilation requires at least one schema declaration: SqlSchema.Create(name, configure), " +
                "SqlSchema.Compile(name, configure), or SqlDatabaseBuilder.Schema(declare) inside " +
                "SqlDatabaseEngineBuilder.AddDatabase(name, configure); found 0.",
                location: null);
            return null;
        }

        var schemas = new List<SchemaSourceModel>();
        var declaredAt = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
        foreach (SchemaDeclaration declaration in declarations)
        {
            (string? name, LambdaExpressionSyntax? configure) = declaration.IsDatabaseBuilderSchema
                ? ReadDatabaseBuilderSchema(declaration)
                : ReadSchemaFactory(declaration);
            if (name is not null && !ValidateDatabaseName(name, declaration, declaredAt))
            {
                name = null;
            }

            if (configure is null)
            {
                continue;
            }

            SchemaSourceModel schema = ExtractSchema(name ?? string.Empty, declaration.Model, configure);
            if (name is not null)
            {
                schemas.Add(schema);
            }
        }

        if (_hasErrors)
        {
            return null;
        }

        return schemas.OrderBy(static schema => schema.Name, StringComparer.Ordinal).ToArray();
    }

    private (string? Name, LambdaExpressionSyntax? Configure) ReadSchemaFactory(SchemaDeclaration declaration)
    {
        (InvocationExpressionSyntax invocation, IMethodSymbol method, SemanticModel model, _) = declaration;
        ExpressionSyntax? nameExpression = GetArgument(invocation, method, "name", 0);
        string? name = nameExpression is null ? null : ConstantString(model, nameExpression);
        if (string.IsNullOrWhiteSpace(name))
        {
            Error("COHDBSDK102", $"SqlSchema.{method.Name} name must be a non-empty compile-time string constant.", nameExpression?.GetLocation() ?? invocation.GetLocation());
            name = null;
        }

        ExpressionSyntax? configureExpression = GetArgument(invocation, method, "configure", 1);
        LambdaExpressionSyntax? configure = UnwrapLambda(configureExpression);
        if (configure is null)
        {
            Error("COHDBSDK103", $"SqlSchema.{method.Name} configuration must be an inline lambda so the build can analyze it without executing Program.Main.", configureExpression?.GetLocation() ?? invocation.GetLocation());
        }

        return (name, configure);
    }

    private (string? Name, LambdaExpressionSyntax? Configure) ReadDatabaseBuilderSchema(SchemaDeclaration declaration)
    {
        (InvocationExpressionSyntax invocation, IMethodSymbol method, SemanticModel model, _) = declaration;
        string? name = EnclosingDatabaseName(model, invocation);
        if (string.IsNullOrWhiteSpace(name))
        {
            Error(
                "COHDBSDK102",
                "SqlDatabaseBuilder.Schema(declare) must be called on the callback parameter of " +
                "SqlDatabaseEngineBuilder.AddDatabase(name, configure), and that name must be a non-empty compile-time " +
                "string constant, so the build can name the database without executing Program.Main.",
                invocation.GetLocation());
            name = null;
        }

        ExpressionSyntax? declareExpression = GetArgument(invocation, method, "declare", 0);
        LambdaExpressionSyntax? configure = UnwrapLambda(declareExpression);
        if (configure is null)
        {
            Error("COHDBSDK103", "SqlDatabaseBuilder.Schema declaration must be an inline lambda so the build can analyze it without executing Program.Main.", declareExpression?.GetLocation() ?? invocation.GetLocation());
        }

        return (name, configure);
    }

    // The database a SqlDatabaseBuilder.Schema(declare) call declares: the call's receiver must be
    // the parameter of a lambda passed to SqlDatabaseEngineBuilder.AddDatabase(name, configure).
    private static string? EnclosingDatabaseName(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Expression: ExpressionSyntax receiver } ||
            model.GetSymbolInfo(receiver).Symbol is not IParameterSymbol parameter)
        {
            return null;
        }

        for (SyntaxNode? node = invocation.Parent; node is not null; node = node.Parent)
        {
            if (node is not LambdaExpressionSyntax lambda || !DeclaresParameter(model, lambda, parameter))
            {
                continue;
            }

            SyntaxNode? argument = lambda.Parent;
            while (argument is ParenthesizedExpressionSyntax)
            {
                argument = argument.Parent;
            }

            if (argument is not ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax outer } } ||
                ResolveMethod(model, outer) is not { } outerMethod ||
                !IsAddDatabase(outerMethod))
            {
                return null;
            }

            ExpressionSyntax? nameExpression = GetArgument(outer, outerMethod, "name", 0);
            return nameExpression is null ? null : ConstantString(model, nameExpression);
        }

        return null;
    }

    private static bool DeclaresParameter(SemanticModel model, LambdaExpressionSyntax lambda, IParameterSymbol parameter)
    {
        if (lambda is SimpleLambdaExpressionSyntax simple)
        {
            return SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(simple.Parameter), parameter);
        }

        return lambda is ParenthesizedLambdaExpressionSyntax parenthesized &&
            parenthesized.ParameterList.Parameters.Any(candidate =>
                SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(candidate), parameter));
    }

    private bool ValidateDatabaseName(string name, SchemaDeclaration declaration, Dictionary<string, Location> declaredAt)
    {
        Location location = declaration.Invocation.GetLocation();
        if (name.IndexOfAny(_invalidFileNameCharacters) >= 0 ||
            name.Any(char.IsControl) ||
            name is "." or ".." ||
            name.EndsWith('.') ||
            name.EndsWith(' '))
        {
            Error(
                "COHDBSDK102",
                $"Database name '{name}' cannot name its schema artifact '{name}.schema.json': a database name cannot contain " +
                "'\\', '/', ':', '*', '?', '\"', '<', '>', '|' or a control character, or end with '.' or a space.",
                location);
            return false;
        }

        if (declaredAt.TryGetValue(name, out Location? first))
        {
            FileLinePositionSpan span = first.GetLineSpan();
            Error(
                "COHDBSDK101",
                $"Database '{name}' has more than one schema declaration; the first is at {span.Path}({span.StartLinePosition.Line + 1}). " +
                "A database has exactly one: SqlSchema.Create(name, configure), SqlSchema.Compile(name, configure), or " +
                "SqlDatabaseBuilder.Schema(declare). The SDK writes one artifact per database name, so a name is unique " +
                "across every engine of the project, and names compare ignoring case.",
                location);
            return false;
        }

        declaredAt.Add(name, location);
        return true;
    }

    private SchemaSourceModel ExtractSchema(string name, SemanticModel model, LambdaExpressionSyntax configure)
    {
        var types = new List<SchemaTypeSource>();
        var tables = new List<SchemaTableSource>();
        var principals = new List<SchemaPrincipalSource>();
        bool allowsDestructiveChanges = false;

        foreach (InvocationExpressionSyntax invocation in configure.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            IMethodSymbol? method = ResolveMethod(model, invocation);
            if (method is null || !IsOnNamedType(method, schemaBuilderType))
            {
                continue;
            }

            switch (method.Name)
            {
                case "AllowDestructiveChanges":
                    allowsDestructiveChanges = true;
                    break;
                case "Type":
                    ReportUnprovisionable(method, invocation);
                    types.Add(ExtractType(model, invocation, method));
                    break;
                case "Table":
                    tables.Add(ExtractTable(model, invocation, method));
                    break;
                case "Principal":
                    ReportUnprovisionable(method, invocation);
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
        ValidateUnique(principals.Select(static item => item.Name), "principal", configure.GetLocation());
        ValidateTables(types, tables, configure.GetLocation());
        ValidateGrants(tables, principals, configure.GetLocation());

        return new SchemaSourceModel(
            name,
            allowsDestructiveChanges,
            types.OrderBy(static item => item.TypeName, StringComparer.Ordinal).ToArray(),
            tables.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray(),
            principals.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray());
    }

    // A declaration every SQL engine build refuses before it touches a file (COHSQLP001, owner
    // decision 58 of 2026-10-09) fails the project's build instead of its first start. It is still
    // extracted, so the other checks do not report what its absence would cause.
    private void ReportUnprovisionable(IMethodSymbol method, InvocationExpressionSyntax invocation)
    {
        Error(
            "COHDBSDK108",
            $"SqlSchemaBuilder.{method.Name} declares what no SQL engine can provision yet: every engine build refuses it " +
            "before touching any file (COHSQLP001, owner decision 58). Remove it; principals, grants and custom types " +
            "wait for their DDL.",
            invocation.GetLocation());
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
            if (operationMethod is null || !IsOnNamedType(operationMethod, typeBuilderType))
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
            return new SchemaTableSource(tableName, TypeName(rowType), [], null, [], [], []);
        }

        var columns = new List<SchemaColumnSource>();
        string? primaryKey = null;
        var indexes = new List<string>();
        var references = new List<SchemaReferenceSource>();
        var checks = new List<SchemaCheckSource>();
        foreach (InvocationExpressionSyntax operation in configure.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            IMethodSymbol? operationMethod = ResolveMethod(model, operation);
            if (operationMethod is null || !IsOnOriginalGenericType(operationMethod, tableBuilderType))
            {
                continue;
            }

            if (operationMethod.Name == "Check")
            {
                if (ExtractCheck(model, operation, operationMethod, tableName) is { } check)
                {
                    checks.Add(check);
                }

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
            references.OrderBy(static item => item.Member, StringComparer.Ordinal).ThenBy(static item => item.TargetType, StringComparer.Ordinal).ToArray(),
            checks);
    }

    // table.Check(name, sql): both arguments compile-time string constants, read as written. The SQL
    // is not parsed here: the task references no SQL parser, and a function the predicate calls is
    // registered on the engine builder, which the SDK never sees. The engine's build binds the
    // predicate before it touches a file (owner decision 64; COHSQLP001).
    private SchemaCheckSource? ExtractCheck(SemanticModel model, InvocationExpressionSyntax operation, IMethodSymbol method, string tableName)
    {
        ExpressionSyntax? nameExpression = GetArgument(operation, method, "name", 0);
        ExpressionSyntax? sqlExpression = GetArgument(operation, method, "sql", 1);
        string? name = nameExpression is null ? null : ConstantString(model, nameExpression);
        string? sql = sqlExpression is null ? null : ConstantString(model, sqlExpression);
        if (string.IsNullOrWhiteSpace(name))
        {
            Error("COHDBSDK102", $"A CHECK on table '{tableName}' needs a non-empty compile-time string constant for its name.",
                nameExpression?.GetLocation() ?? operation.GetLocation());
            return null;
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            Error("COHDBSDK104", $"CHECK '{name}' on table '{tableName}' needs its SQL predicate as a non-empty compile-time string " +
                "constant, so the build can read it without executing Program.Main.",
                sqlExpression?.GetLocation() ?? operation.GetLocation());
            return null;
        }

        return new SchemaCheckSource(name, sql);
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
            if (operationMethod is null || !IsOnNamedType(operationMethod, principalBuilderType))
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
            // A table's constraints, its primary key and its indexes share one name space in the
            // SQL catalog, as the runtime compiler checks.
            var constraintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (table.PrimaryKey is not null)
            {
                constraintNames.Add($"PK_{table.Name}");
            }
            foreach (string index in table.Indexes)
            {
                constraintNames.Add($"IX_{table.Name}_{index}");
            }
            foreach (SchemaReferenceSource reference in table.References)
            {
                if (tables.FirstOrDefault(candidate => string.Equals(candidate.RowType, reference.TargetType, StringComparison.Ordinal)) is { } target)
                {
                    constraintNames.Add($"FK_{table.Name}_{target.Name}_{reference.Member}");
                }
            }
            foreach (SchemaCheckSource check in table.Checks)
            {
                if (!constraintNames.Add(check.Name))
                {
                    Error("COHDBSDK105", $"Table '{table.Name}' declares CHECK '{check.Name}', a name another of its constraints, " +
                        "its primary key or one of its indexes already has.", location);
                }
            }

            foreach (SchemaColumnSource column in table.Columns)
            {
                if (!IsBuiltInType(column.TypeName) && !declaredTypes.Contains(column.TypeName))
                {
                    Error(
                        "COHDBSDK106",
                        $"Column '{table.Name}.{column.Name}' has type '{column.TypeName}', which is not a SQL column type " +
                        "(bool, an integer or floating-point type, decimal, string, Guid, a date or time type, TimeSpan or byte[]).",
                        location);
                }
            }
        }
    }

    private void ValidateGrants(
        IReadOnlyList<SchemaTableSource> tables,
        IReadOnlyList<SchemaPrincipalSource> principals,
        Location location)
    {
        var objects = tables.Select(static item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private static bool IsNullable(ITypeSymbol type)
    {
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
        return new SchemaColumnSource(symbol.Name, TypeName(UnderlyingType(type)), IsNullable(type));
    }

    private static IMethodSymbol? DelegateInvoke(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { Name: "Expression", TypeArguments.Length: 1 } expression)
        {
            type = expression.TypeArguments[0];
        }
        return (type as INamedTypeSymbol)?.DelegateInvokeMethod;
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

    // SqlSchema.Create(name, configure) or SqlSchema.Compile(name, configure).
    private static bool IsSchemaFactory(IMethodSymbol method)
    {
        return method.Name is "Create" or "Compile" &&
            method.Parameters.Length == 2 &&
            IsOnNamedType(method, schemaType) &&
            IsSchemaBuilderCallback(method.Parameters[1].Type);
    }

    // SqlDatabaseBuilder.Schema(Action<SqlSchemaBuilder> declare); its Schema(SqlSchema) overload
    // takes a value whose own SqlSchema.Create is the anchor.
    private static bool IsDatabaseBuilderSchema(IMethodSymbol method)
    {
        return method.Name == "Schema" &&
            method.Parameters.Length == 1 &&
            IsOnNamedType(method, databaseBuilderType) &&
            IsSchemaBuilderCallback(method.Parameters[0].Type);
    }

    // SqlDatabaseEngineBuilder.AddDatabase(string name, Action<SqlDatabaseBuilder>? configure).
    private static bool IsAddDatabase(IMethodSymbol method)
    {
        return method.Name == "AddDatabase" &&
            method.Parameters.Length == 2 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
            IsOnNamedType(method, engineBuilderType);
    }

    private static bool IsSchemaBuilderCallback(ITypeSymbol type)
    {
        IMethodSymbol? configure = DelegateInvoke(type);
        return configure?.Parameters.Length == 1 &&
            string.Equals(DisplayTypeName(configure.Parameters[0].Type), schemaBuilderType, StringComparison.Ordinal);
    }

    private static bool IsOnNamedType(IMethodSymbol method, string typeName)
    {
        return string.Equals(DisplayTypeName(method.ContainingType), typeName, StringComparison.Ordinal);
    }

    private static bool IsOnOriginalGenericType(IMethodSymbol method, string typeName)
    {
        INamedTypeSymbol containingType = method.ContainingType.OriginalDefinition;
        return string.Equals(typeName, tableBuilderType, StringComparison.Ordinal) &&
            string.Equals(containingType.MetadataName, "SqlTableBuilder`1", StringComparison.Ordinal) &&
            string.Equals(containingType.ContainingNamespace.ToDisplayString(), "Assimalign.Cohesion.Database.Sql.Schema", StringComparison.Ordinal);
    }

    private static string TypeName(ITypeSymbol type)
    {
        return CSharpTypeIdentity.Create(type);
    }

    private static string DisplayTypeName(ITypeSymbol type) => type.ToDisplayString(_typeDisplayFormat);

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

    private sealed record SchemaDeclaration(
        InvocationExpressionSyntax Invocation,
        IMethodSymbol Method,
        SemanticModel Model,
        bool IsDatabaseBuilderSchema);
}
