using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.SourceGeneration.Database;

/// <summary>Generates static entity mapping from the application's existing SQL schema declarations.</summary>
/// <remarks>
/// Reads the semantic C# declarations passed to <c>SqlSchema.Create</c> and <c>SqlSchema.Compile</c>.
/// Unsupported executable schema composition produces a diagnostic; no runtime discovery is emitted.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SqlMapperGenerator : IIncrementalGenerator
{
    private const string SchemaNamespace = "Assimalign.Cohesion.Database.Sql.Schema";
    private const string MappingNamespace = "global::Assimalign.Cohesion.Database.Mapping.";
    private const string SqlMappingNamespace = "global::Assimalign.Cohesion.Database.Sql.Mapping.";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var enabled = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.CohesionGenerateDatabaseMappers", out string? value) &&
            bool.TryParse(value, out bool generate) && generate);
        var declarations = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation &&
                    CandidateName(invocation.Expression) is "Create" or "Compile",
                static (syntax, token) => Analyze(syntax, token))
            .Where(static result => result is not null)
            .Collect();

        context.RegisterSourceOutput(declarations.Combine(enabled), static (production, input) =>
        {
            if (input.Right)
            {
                Emit(production, input.Left);
            }
        });
    }

    private static string? CandidateName(ExpressionSyntax expression)
        => expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };

    private static SchemaResult? Analyze(GeneratorSyntaxContext context, CancellationToken token)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol method ||
            method.ContainingType.ToDisplayString() != SchemaNamespace + ".SqlSchema")
        {
            return null;
        }

        // Compiler-only consumers may not reference the runtime contracts. Even when the build
        // flag enables generation, there is no mapping output without those contracts.
        if (context.SemanticModel.Compilation.GetTypeByMetadataName(
                "Assimalign.Cohesion.Database.Mapping.IEntityMapper`3") is null)
        {
            return null;
        }

        var result = new SchemaResult
        {
            HasSqlAdapter = context.SemanticModel.Compilation.GetTypeByMetadataName(
                "Assimalign.Cohesion.Database.Sql.Mapping.ISqlEntityMapping`3") is not null
        };
        ExpressionSyntax? configure = Argument(invocation, method, "configure");
        if (!TryCallback(configure, context.SemanticModel.Compilation, token, out Callback? callback))
        {
            result.Error(SqlMapperDiagnostics.UnsupportedDeclaration, invocation,
                "Schema configuration must be an inline lambda or a source-declared method with one builder parameter.");
            return result;
        }

        if (!TryStatements(callback!, out IReadOnlyList<InvocationExpressionSyntax> calls))
        {
            result.Error(SqlMapperDiagnostics.UnsupportedDeclaration, callback!.Body,
                "Schema configuration must contain only direct builder calls; conditions, loops, aliases and executable composition are not supported.");
            return result;
        }

        foreach (InvocationExpressionSyntax call in calls)
        {
            token.ThrowIfCancellationRequested();
            if (!TryBuilderCall(call, callback!, token, out IMethodSymbol? called) ||
                called!.ContainingType.ToDisplayString() != SchemaNamespace + ".ISqlSchemaBuilder")
            {
                result.Error(SqlMapperDiagnostics.UnsupportedDeclaration, call,
                    "Schema configuration must call the supplied ISqlSchemaBuilder directly.");
                continue;
            }

            if (called.Name == "Table")
            {
                AnalyzeTable(call, called, callback!.Model, result, token);
            }
            else if (called.Name == "Type")
            {
                result.CustomTypes.Add(called.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString());
            }
        }

        ValidateRelationships(result);
        return result;
    }

    private static void AnalyzeTable(InvocationExpressionSyntax invocation, IMethodSymbol method,
        SemanticModel model, SchemaResult result, CancellationToken token)
    {
        if (method.TypeArguments[0] is not INamedTypeSymbol entity || entity.TypeKind != TypeKind.Class ||
            entity.IsAbstract || entity.IsFileLocal || !IsAccessible(entity, model.Compilation) || ContainsGenericType(entity) ||
            !entity.InstanceConstructors.Any(constructor => constructor.Parameters.Length == 0 && IsAccessible(constructor, model.Compilation)))
        {
            result.Error(SqlMapperDiagnostics.UnsupportedEntity, invocation,
                "A mapped entity must be an accessible, non-generic, concrete class with an accessible parameterless constructor.");
            return;
        }

        string tableName = entity.Name;
        ExpressionSyntax? name = Argument(invocation, method, "name");
        if (name is not null)
        {
            Optional<object?> constant = model.GetConstantValue(name, token);
            if (!constant.HasValue || constant.Value is not string text || string.IsNullOrWhiteSpace(text))
            {
                result.Error(SqlMapperDiagnostics.UnsupportedDeclaration, name, "Table names must be non-empty compile-time constants.");
                return;
            }

            tableName = text;
        }

        if (result.HasSqlAdapter && (tableName.IndexOf('"') >= 0 || tableName.IndexOf('\0') >= 0))
        {
            result.Error(SqlMapperDiagnostics.InvalidRelationalSchema, invocation,
                "SQL mapper table identifiers cannot contain a double quote or NUL because the engine cannot parse escaped quoted identifiers.");
            return;
        }

        if (!TryCallback(Argument(invocation, method, "configure"), model.Compilation, token, out Callback? callback) ||
            !TryStatements(callback!, out IReadOnlyList<InvocationExpressionSyntax> calls))
        {
            result.Error(SqlMapperDiagnostics.UnsupportedDeclaration, invocation,
                "Table configuration must be a source-declared callback containing only direct builder calls.");
            return;
        }

        var columns = new List<Column>();
        var references = new List<Reference>();
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Column? key = null;
        bool valid = true;
        foreach (InvocationExpressionSyntax call in calls)
        {
            if (!TryBuilderCall(call, callback!, token, out IMethodSymbol? called) ||
                called!.ContainingType.OriginalDefinition.ToDisplayString() != SchemaNamespace + ".ISqlTableBuilder<TRow>" ||
                !TrySelector(Argument(call, called, "selector"), callback!.Model, token, out ISymbol? member))
            {
                result.Error(SqlMapperDiagnostics.UnsupportedDeclaration, call,
                    "Table calls must select a direct entity field or property using the supplied table builder.");
                valid = false;
                continue;
            }

            ITypeSymbol? type = MemberType(member!);
            if (member!.Name is "Snapshot" or "Matches" or "BytesEqual" || member.Name.StartsWith("_value", StringComparison.Ordinal) ||
                (result.HasSqlAdapter && member.Name == "Columns"))
            {
                result.Error(SqlMapperDiagnostics.UnsupportedEntity, call,
                    "Mapped member '" + member.Name + "' conflicts with a generated snapshot member name.");
                valid = false;
                continue;
            }
            if (type is null || !IsAccessible(member!, model.Compilation) || !IsWritable(member!, model.Compilation) || !IsSupported(type) ||
                member is IPropertySymbol { ReturnsByRef: true } or IPropertySymbol { ReturnsByRefReadonly: true })
            {
                result.Error(SqlMapperDiagnostics.UnsupportedEntity, call,
                    "Mapped member '" + member!.Name + "' must be a readable/writable scalar or byte[] field/property. Custom scalar conversions and navigation members require a model-specific mapper.");
                valid = false;
                continue;
            }

            Column? column = columns.FirstOrDefault(value => value.Name == member!.Name);
            if (column is null)
            {
                if (columns.Any(value => string.Equals(value.Name, member.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Error(SqlMapperDiagnostics.InvalidRelationalSchema, call,
                        "Column '" + member.Name + "' duplicates a declared column under the SQL schema's case-insensitive name rules.");
                    valid = false;
                }
                column = new Column(member!.Name, type);
                columns.Add(column);
            }

            if (called.Name == "Key" || called.Name == "PrimaryKey")
            {
                key = column; // Matches the retained builder's last primary-key declaration.
            }
            else if (called.Name == "Index" && !indexes.Add(column.Name))
            {
                result.Error(SqlMapperDiagnostics.InvalidRelationalSchema, call,
                    "Index on '" + tableName + "." + column.Name + "' is declared more than once.");
                valid = false;
            }
            else if (called.Name == "References")
            {
                string target = called.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (references.Any(reference => reference.Column.Name == column.Name && reference.TargetEntity == target))
                {
                    result.Error(SqlMapperDiagnostics.InvalidRelationalSchema, call,
                        "Reference from '" + tableName + "." + column.Name + "' to '" + target + "' is declared more than once.");
                    valid = false;
                }
                references.Add(new Reference(column, target, call.GetLocation()));
            }
        }

        if (key is null || key.IsBinary || IsNullable(key.Type))
        {
            result.Error(SqlMapperDiagnostics.InvalidKey, invocation,
                "A generated mapper requires one declared non-null immutable scalar primary key; byte[] and nullable keys are unsupported.");
            valid = false;
        }
        else if (result.HasSqlAdapter && (key.Type.SpecialType is SpecialType.System_Single or SpecialType.System_Double ||
                 key.IsDateTime || key.IsDateTimeOffset))
        {
            result.Error(SqlMapperDiagnostics.InvalidKey, invocation,
                key.IsDateTime || key.IsDateTimeOffset
                    ? "SQL equality ignores DateTime.Kind and DateTimeOffset.Offset while stored keys preserve them; SQL mappings require another immutable scalar primary-key type."
                    : "SQL equality converts floating-point operands to Decimal and cannot preserve every stored floating-point key identity; SQL mappings require another immutable scalar primary-key type.");
            valid = false;
        }

        for (INamedTypeSymbol? current = entity; current is not null; current = current.BaseType)
        {
            if (current.GetMembers().Any(member => (member is IPropertySymbol { IsRequired: true } ||
                    member is IFieldSymbol { IsRequired: true }) && !columns.Any(column => column.Name == member.Name)))
            {
                result.Error(SqlMapperDiagnostics.UnsupportedEntity, invocation,
                    "Every required entity member must be selected by the retained schema.");
                valid = false;
            }
        }

        if (valid)
        {
            result.Tables.Add(new Table(entity, tableName, columns, key!, references, indexes,
                result.HasSqlAdapter, invocation.GetLocation()));
        }
    }

    private static void ValidateRelationships(SchemaResult result)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entities = new Dictionary<string, Table>(StringComparer.Ordinal);
        foreach (Table table in result.Tables)
        {
            if (!names.Add(table.Name) || entities.ContainsKey(table.EntityName))
            {
                result.Diagnostics.Add(Diagnostic.Create(SqlMapperDiagnostics.InvalidRelationalSchema, table.Location,
                    "Table '" + table.Name + "' or row type '" + table.EntityName + "' is declared more than once in one schema."));
            }
            else
            {
                entities.Add(table.EntityName, table);
            }
        }

        foreach (Table table in result.Tables)
        {
            var constraintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Column column in table.Columns)
            {
                if (result.CustomTypes.Contains(ScalarTypeIdentity(column.Type)))
                {
                    result.Diagnostics.Add(Diagnostic.Create(SqlMapperDiagnostics.InvalidRelationalSchema, table.Location,
                        "Mapped member '" + table.Name + "." + column.Name + "' uses a custom schema storage override; generated scalar conversions cannot represent it."));
                }
            }
            foreach (Reference reference in table.References)
            {
                if (!entities.TryGetValue(reference.TargetEntity, out Table? target))
                {
                    result.Diagnostics.Add(Diagnostic.Create(SqlMapperDiagnostics.InvalidRelationalSchema, reference.Location,
                        "Reference '" + table.Name + "." + reference.Column.Name + "' requires a mapped target with a primary key in the same schema: '" + reference.TargetEntity + "'."));
                }
                else if (StorageType(reference.Column.Type) != StorageType(target.Key.Type))
                {
                    result.Diagnostics.Add(Diagnostic.Create(SqlMapperDiagnostics.InvalidRelationalSchema, reference.Location,
                        "Foreign-key storage type for '" + table.Name + "." + reference.Column.Name + "' does not match '" + target.Name + "." + target.Key.Name + "'."));
                }
                else
                {
                    reference.TargetTable = target.Name;
                    reference.TargetKey = target.Key.Name;
                    if (!constraintNames.Add("FK_" + table.Name + "_" + target.Name + "_" + reference.Column.Name))
                    {
                        result.Diagnostics.Add(Diagnostic.Create(SqlMapperDiagnostics.InvalidRelationalSchema, reference.Location,
                            "Foreign-key declarations produce a duplicate constraint name on table '" + table.Name + "'."));
                    }
                }
            }
        }
    }

    private static string StorageType(ITypeSymbol type)
        => ScalarTypeIdentity(type) == "byte" ? "short" : ScalarTypeIdentity(type);

    private static string ScalarTypeIdentity(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }
        return type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString();
    }

    private static bool TryCallback(ExpressionSyntax? expression, Compilation compilation,
        CancellationToken token, out Callback? callback)
    {
        callback = null;
        if (expression is null)
        {
            return false;
        }

        SemanticModel model = compilation.GetSemanticModel(expression.SyntaxTree);
        SyntaxNode? body;
        IParameterSymbol? parameter;
        if (expression is LambdaExpressionSyntax lambda)
        {
            body = lambda.Body;
            parameter = (model.GetSymbolInfo(lambda, token).Symbol as IMethodSymbol)?.Parameters.SingleOrDefault();
        }
        else if (model.GetSymbolInfo(expression, token).Symbol is IMethodSymbol { Parameters.Length: 1 } method &&
                 method.DeclaringSyntaxReferences.Length == 1)
        {
            SyntaxNode declaration = method.DeclaringSyntaxReferences[0].GetSyntax(token);
            body = declaration switch
            {
                MethodDeclarationSyntax member => (SyntaxNode?)member.Body ?? member.ExpressionBody?.Expression,
                LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody?.Expression,
                _ => null
            };
            parameter = method.Parameters[0];
            model = compilation.GetSemanticModel(declaration.SyntaxTree);
        }
        else
        {
            return false;
        }

        if (body is null || parameter is null)
        {
            return false;
        }

        callback = new Callback(body, parameter, model);
        return true;
    }

    private static bool TryStatements(Callback callback, out IReadOnlyList<InvocationExpressionSyntax> calls)
    {
        var result = new List<InvocationExpressionSyntax>();
        calls = result;
        if (callback.Body is InvocationExpressionSyntax expression)
        {
            result.Add(expression);
            return true;
        }

        if (callback.Body is not BlockSyntax block)
        {
            return false;
        }

        foreach (StatementSyntax statement in block.Statements)
        {
            if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation })
            {
                return false;
            }

            result.Add(invocation);
        }

        return true;
    }

    private static bool TryBuilderCall(InvocationExpressionSyntax invocation, Callback callback,
        CancellationToken token, out IMethodSymbol? method)
    {
        method = callback.Model.GetSymbolInfo(invocation, token).Symbol as IMethodSymbol;
        return method is not null && invocation.Expression is MemberAccessExpressionSyntax member &&
            SymbolEqualityComparer.Default.Equals(callback.Model.GetSymbolInfo(member.Expression, token).Symbol, callback.Parameter);
    }

    private static bool TrySelector(ExpressionSyntax? expression, SemanticModel model,
        CancellationToken token, out ISymbol? member)
    {
        member = null;
        if (expression is not LambdaExpressionSyntax lambda || lambda.Body is not ExpressionSyntax body ||
            model.GetSymbolInfo(lambda, token).Symbol is not IMethodSymbol { Parameters.Length: 1 } method)
        {
            return false;
        }

        int conversions = 0;
        while (body is ParenthesizedExpressionSyntax || body is CastExpressionSyntax)
        {
            if (body is CastExpressionSyntax cast &&
                (model.GetTypeInfo(cast.Type, token).Type?.SpecialType != SpecialType.System_Object || ++conversions > 1))
            {
                return false;
            }
            body = body is ParenthesizedExpressionSyntax parentheses ? parentheses.Expression : ((CastExpressionSyntax)body).Expression;
        }

        if (body is not MemberAccessExpressionSyntax access ||
            !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression, token).Symbol, method.Parameters[0]))
        {
            return false;
        }

        member = model.GetSymbolInfo(access, token).Symbol;
        return member is IPropertySymbol or IFieldSymbol;
    }

    private static ExpressionSyntax? Argument(InvocationExpressionSyntax invocation, IMethodSymbol method, string name)
    {
        for (int index = 0; index < invocation.ArgumentList.Arguments.Count; index++)
        {
            ArgumentSyntax argument = invocation.ArgumentList.Arguments[index];
            if (argument.NameColon?.Name.Identifier.ValueText == name ||
                (argument.NameColon is null && index < method.Parameters.Length && method.Parameters[index].Name == name))
            {
                return argument.Expression;
            }
        }

        return null;
    }

    private static bool IsAccessible(ISymbol symbol, Compilation compilation)
        => compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly);

    private static bool ContainsGenericType(INamedTypeSymbol type)
        => type.IsGenericType || (type.ContainingType is not null && ContainsGenericType(type.ContainingType));

    private static ITypeSymbol? MemberType(ISymbol member)
        => member switch { IPropertySymbol property => property.Type, IFieldSymbol field => field.Type, _ => null };

    private static bool IsWritable(ISymbol member, Compilation compilation)
        => !member.IsStatic && member switch
        {
            IPropertySymbol property => !property.IsIndexer && property.GetMethod is not null &&
                property.SetMethod is not null && IsAccessible(property.GetMethod, compilation) && IsAccessible(property.SetMethod, compilation),
            IFieldSymbol field => !field.IsReadOnly && !field.IsConst,
            _ => false
        };

    private static bool IsNullable(ITypeSymbol type)
        => type.NullableAnnotation == NullableAnnotation.Annotated ||
            type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static bool IsSupported(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return IsSupported(nullable.TypeArguments[0]);
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return true;
        }

        return type.SpecialType is SpecialType.System_Boolean or SpecialType.System_SByte or SpecialType.System_Byte or
            SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Single or
            SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_String ||
            type.ToDisplayString() is "System.DateOnly" or "System.TimeOnly" or "System.DateTime" or
                "System.DateTimeOffset" or "System.TimeSpan" or "System.Guid";
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<SchemaResult?> results)
    {
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, Table>(StringComparer.Ordinal);
        foreach (SchemaResult? result in results)
        {
            foreach (Diagnostic diagnostic in result!.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }

            foreach (Table table in result.Diagnostics.Count == 0 ? result.Tables : Enumerable.Empty<Table>())
            {
                string generatedName = table.Entity.ContainingNamespace.ToDisplayString() + "." + table.MapperName;
                if (names.TryGetValue(generatedName, out Table? named) && named.EntityName != table.EntityName)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SqlMapperDiagnostics.ConflictingDeclaration, table.Location,
                        "Entities '" + named.EntityName + "' and '" + table.EntityName + "' generate the same mapper name."));
                    conflicts.Add(named.EntityName);
                    conflicts.Add(table.EntityName);
                }
                else if (table.Entity.ContainingNamespace.GetTypeMembers(table.MapperName).Length != 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SqlMapperDiagnostics.ConflictingDeclaration, table.Location,
                        "Generated mapper name '" + generatedName + "' is already declared in the application."));
                    conflicts.Add(table.EntityName);
                }
                names[generatedName] = table;
                if (tables.TryGetValue(table.EntityName, out Table? previous) && previous.Signature != table.Signature)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SqlMapperDiagnostics.ConflictingDeclaration, table.Location,
                        "Entity '" + table.EntityName + "' has conflicting table, member-order, primary-key, index or foreign-key declarations."));
                    conflicts.Add(table.EntityName);
                }
                else
                {
                    tables[table.EntityName] = table;
                }
            }
        }

        foreach (Table table in tables.Values.OrderBy(table => table.EntityName, StringComparer.Ordinal))
        {
            if (!conflicts.Contains(table.EntityName))
            {
                context.AddSource(HintName(table),
                    SourceText.From(Generate(table), Encoding.UTF8));
            }
        }
    }

    private static string HintName(Table table)
    {
        using SHA256 hash = SHA256.Create();
        string suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(table.EntityName))).Replace("-", "");
        return table.MapperName.Substring(0, Math.Min(table.MapperName.Length, 64)) + "_" + suffix + ".g.cs";
    }

    private static string Generate(Table table)
    {
        var text = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!table.Entity.ContainingNamespace.IsGlobalNamespace)
        {
            text.Append("namespace ").Append(table.Entity.ContainingNamespace.ToDisplayString()).AppendLine(";");
        }

        string entity = table.EntityName;
        string key = table.Key.TypeName;
        string mapper = table.MapperName;
        text.AppendLine("/// <summary>Maps the retained SQL schema's declared values and captures detached entity state.</summary>");
        text.Append(table.IsPublic ? "public" : "internal").Append(" sealed class ").Append(mapper).Append(" : ");
        if (table.HasSqlAdapter)
        {
            text.Append(SqlMappingNamespace).Append("ISqlEntityMapping<").Append(entity).Append(", ").Append(key).Append(", ")
                .Append(mapper).AppendLine(".Snapshot>\n{");
            GenerateSqlMapping(text, table);
        }
        else
        {
            text.Append(MappingNamespace).Append("IEntityMapper<").Append(entity).Append(", ").Append(key).Append(", ")
            .Append(mapper).Append(".Snapshot>, ").Append(MappingNamespace).Append("IEntityReader<").Append(entity)
            .Append(", global::System.Collections.Generic.IReadOnlyList<object?>>, ").Append(MappingNamespace)
            .Append("IEntityWriter<").Append(entity).AppendLine(", global::System.Collections.Generic.IList<object?>>\n{");
        }
        text.AppendLine("    /// <inheritdoc />").Append("    public ").Append(key).Append(" GetKey(").Append(entity).AppendLine(" entity)\n    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(entity);")
            .Append("        return entity.@").Append(table.Key.Name);
        if (table.Key.Type.IsReferenceType)
        {
            text.Append(" ?? throw new global::System.InvalidOperationException(\"Entity keys cannot be null.\")");
        }
        text.AppendLine(";\n    }");
        text.AppendLine("    /// <inheritdoc />").Append("    public Snapshot Capture(").Append(entity).AppendLine(" entity)\n    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(entity);\n        return new Snapshot(entity);\n    }");
        text.AppendLine("    /// <inheritdoc />\n    public bool AreEqual(Snapshot left, Snapshot right)\n    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(left);\n        global::System.ArgumentNullException.ThrowIfNull(right);\n        return left.Matches(right);\n    }");
        text.AppendLine("    /// <inheritdoc />").Append("    public ").Append(entity)
            .AppendLine(" Read(global::System.Collections.Generic.IReadOnlyList<object?> source)\n    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(source);")
            .Append("        if (source.Count != ").Append(table.Columns.Count).AppendLine(") throw new global::System.ArgumentException(\"The value count must match the retained schema.\", nameof(source));")
            .Append("        return new ").Append(entity).AppendLine("\n        {");
        for (int index = 0; index < table.Columns.Count; index++)
        {
            Column column = table.Columns[index];
            text.Append("            @").Append(column.Name).Append(" = ");
            if (column.IsBinary)
            {
                text.Append("source[").Append(index).Append("] is null ? null! : (byte[])((byte[])source[").Append(index).Append("]!).Clone()");
            }
            else
            {
                if (column.IsByte)
                {
                    text.Append("checked((").Append(column.TypeName).Append(')');
                }
                text.Append('(').Append(column.IsByte ? (IsNullable(column.Type) ? "short?" : "short") : column.TypeName)
                    .Append(")source[").Append(index).Append("]!");
                if (column.IsByte)
                {
                    text.Append(')');
                }
            }
            text.AppendLine(",");
        }
        text.AppendLine("        };\n    }");
        text.AppendLine("    /// <inheritdoc />").Append("    public void Write(").Append(entity)
            .AppendLine(" entity, global::System.Collections.Generic.IList<object?> target)\n    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(entity);\n        global::System.ArgumentNullException.ThrowIfNull(target);")
            .Append("        if (target.Count != ").Append(table.Columns.Count).AppendLine(") throw new global::System.ArgumentException(\"The value count must match the retained schema.\", nameof(target));");
        for (int index = 0; index < table.Columns.Count; index++)
        {
            Column column = table.Columns[index];
            text.Append("        target[").Append(index).Append("] = ");
            if (column.IsByte)
            {
                text.Append(IsNullable(column.Type) ? "(short?)" : "(short)");
            }
            text.Append("entity.@").Append(column.Name);
            if (column.IsBinary)
            {
                text.Append(" is null ? null : (byte[])entity.@").Append(column.Name).Append(".Clone()");
            }
            text.AppendLine(";");
        }
        text.AppendLine("    }");
        text.AppendLine("    /// <summary>Owns the detached values of explicitly declared schema members.</summary>\n    public sealed class Snapshot\n    {");
        for (int index = 0; index < table.Columns.Count; index++)
        {
            text.Append("        private readonly ").Append(table.Columns[index].TypeName).Append(" _value").Append(index).AppendLine(";");
            text.AppendLine("        /// <summary>Gets the captured member value, copying mutable binary data.</summary>")
                .Append("        public ").Append(table.Columns[index].TypeName).Append(" @").Append(table.Columns[index].Name)
                .Append(" => ");
            if (table.Columns[index].IsBinary)
            {
                text.Append("_value").Append(index).Append(" is null ? null! : (byte[])_value").Append(index).AppendLine(".Clone();");
            }
            else
            {
                text.Append("_value").Append(index).AppendLine(";");
            }
        }
        text.Append("        internal Snapshot(").Append(entity).AppendLine(" entity)\n        {");
        for (int index = 0; index < table.Columns.Count; index++)
        {
            Column column = table.Columns[index];
            text.Append("            _value").Append(index).Append(" = entity.@").Append(column.Name);
            if (column.IsBinary)
            {
                text.Append(" is null ? null! : (byte[])entity.@").Append(column.Name).Append(".Clone()");
            }
            text.AppendLine(";");
        }
        text.AppendLine("        }\n        internal bool Matches(Snapshot other)\n        {");
        text.Append("            return ");
        for (int index = 0; index < table.Columns.Count; index++)
        {
            if (index > 0) text.Append("\n                && ");
            Column column = table.Columns[index];
            if (column.IsBinary)
            {
                text.Append("BytesEqual(_value").Append(index).Append(", other._value").Append(index).Append(')');
            }
            else if (column.IsDateTime || column.IsDateTimeOffset)
            {
                string left = "_value" + index;
                string right = "other._value" + index;
                if (IsNullable(column.Type))
                {
                    text.Append('(').Append(left).Append(".HasValue == ").Append(right).Append(".HasValue && (!")
                        .Append(left).Append(".HasValue || ");
                    left += ".Value";
                    right += ".Value";
                }

                if (column.IsDateTime)
                {
                    text.Append('(').Append(left).Append(".Ticks == ").Append(right).Append(".Ticks && ")
                        .Append(left).Append(".Kind == ").Append(right).Append(".Kind)");
                }
                else
                {
                    text.Append(left).Append(".EqualsExact(").Append(right).Append(')');
                }

                if (IsNullable(column.Type))
                {
                    text.Append("))");
                }
            }
            else
            {
                text.Append("global::System.Collections.Generic.EqualityComparer<").Append(column.TypeName)
                    .Append(">.Default.Equals(_value").Append(index).Append(", other._value").Append(index).Append(')');
            }
        }
        text.AppendLine(";\n        }");
        if (table.Columns.Any(column => column.IsBinary))
        {
            text.AppendLine("        private static bool BytesEqual(byte[]? left, byte[]? right)\n        {\n            if (left is null || right is null) return left is null && right is null;\n            if (left.Length != right.Length) return false;\n            for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;\n            return true;\n        }");
        }
        return text.AppendLine("    }\n}").ToString();
    }

    private static void GenerateSqlMapping(StringBuilder text, Table table)
    {
        GenerateSchemaTable(text, table);
        text.AppendLine("    /// <inheritdoc />")
            .Append("    public string TableName => ").Append(Literal(table.Name)).AppendLine(";")
            .AppendLine("    /// <inheritdoc />")
            .Append("    public string KeyColumnName => ").Append(Literal(table.Key.Name)).AppendLine(";")
            .AppendLine("    /// <inheritdoc />")
            .Append("    public global::System.Collections.Generic.IReadOnlyList<string> ColumnNames { get; } = global::System.Array.AsReadOnly(new string[] { ")
            .Append(string.Join(", ", table.Columns.Select(column => Literal(column.Name)))).AppendLine(" });")
            .AppendLine("    /// <inheritdoc />")
            .Append("    public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Types.DatabaseType> ColumnTypes { get; } = global::System.Array.AsReadOnly(new global::Assimalign.Cohesion.Database.Types.DatabaseType[] { ")
            .Append(string.Join(", ", table.Columns.Select(column => "global::Assimalign.Cohesion.Database.Types.DatabaseType." + DatabaseTypeName(column.Type))))
            .AppendLine(" });")
            .AppendLine("    /// <inheritdoc />")
            .Append("    public global::System.Collections.Generic.IReadOnlyList<string> ReferencedTables { get; } = global::System.Array.AsReadOnly(new string[] { ")
            .Append(string.Join(", ", table.References.Select(reference => reference.TargetTable).Distinct(StringComparer.OrdinalIgnoreCase).Select(Literal)))
            .AppendLine(" });")
            .AppendLine("    /// <inheritdoc />")
            .AppendLine("    public void WriteSnapshot(Snapshot snapshot, global::System.Collections.Generic.IList<object?> target)\n    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(snapshot);\n        global::System.ArgumentNullException.ThrowIfNull(target);")
            .Append("        if (target.Count != ").Append(table.Columns.Count)
            .AppendLine(") throw new global::System.ArgumentException(\"The value count must match the retained schema.\", nameof(target));");
        for (int index = 0; index < table.Columns.Count; index++)
        {
            Column column = table.Columns[index];
            text.Append("        target[").Append(index).Append("] = ");
            if (column.IsByte)
            {
                text.Append(IsNullable(column.Type) ? "(short?)" : "(short)");
            }
            text.Append("snapshot.@").Append(column.Name).AppendLine(";");
        }
        text.AppendLine("    }")
            .AppendLine("    /// <summary>Provides typed query columns from the retained table declaration.</summary>")
            .AppendLine("    public static class Columns\n    {");
        foreach (Column column in table.Columns)
        {
            text.AppendLine("        /// <summary>Gets the retained column for typed SQL predicates and ordering.</summary>")
                .Append("        public static ").Append(SqlMappingNamespace).Append("SqlColumn<").Append(table.EntityName).Append(", ")
                .Append(column.TypeName).Append("> @").Append(column.Name).Append(" { get; } = new(")
                .Append(Literal(table.Name)).Append(", ").Append(Literal(column.Name)).AppendLine(");");
        }
        text.AppendLine("    }");
    }

    private static void GenerateSchemaTable(StringBuilder text, Table table)
    {
        const string schema = "global::" + SchemaNamespace + ".";
        text.AppendLine("    /// <summary>Gets the immutable compiled table from the retained declaration for reflection-free deployment.</summary>")
            .Append("    public static ").Append(schema).Append("CompiledSchemaTable SchemaTable { get; } = new(")
            .Append(Literal(table.Name)).Append(", ").Append(Literal(RowTypeIdentity(table.Entity))).AppendLine(",")
            .Append("        new ").Append(schema).AppendLine("CompiledSchemaColumn[]\n        {");
        foreach (Column column in table.Columns)
        {
            text.Append("            new(").Append(Literal(column.Name)).Append(", global::Assimalign.Cohesion.Database.Types.DatabaseType.")
                .Append(DatabaseTypeName(column.Type)).Append(", ")
                .Append(column.Type.IsReferenceType || IsNullable(column.Type) ? "true" : "false").AppendLine("),");
        }
        text.AppendLine("        },")
            .Append("        new ").Append(schema).Append("CompiledSchemaKey(").Append(Literal("PK_" + table.Name))
            .Append(", new string[] { ").Append(Literal(table.Key.Name)).AppendLine(" }),")
            .Append("        new ").Append(schema).AppendLine("CompiledSchemaIndex[]\n        {");
        foreach (string index in table.Indexes.OrderBy(value => value, StringComparer.Ordinal))
        {
            text.Append("            new(").Append(Literal("IX_" + table.Name + "_" + index))
                .Append(", new string[] { ").Append(Literal(index)).AppendLine(" }),");
        }
        text.AppendLine("        },")
            .Append("        new ").Append(schema).AppendLine("CompiledSchemaConstraint[]\n        {");
        foreach (Reference reference in table.References)
        {
            text.Append("            new(").Append(Literal("FK_" + table.Name + "_" + reference.TargetTable + "_" + reference.Column.Name))
                .Append(", ").Append(schema).Append("CompiledSchemaConstraintKind.Reference, new string[] { ")
                .Append(Literal(reference.Column.Name)).Append(" }, ").Append(Literal(reference.TargetTable))
                .Append(", new string[] { ").Append(Literal(reference.TargetKey)).AppendLine(" }),");
        }
        text.AppendLine("        });");
    }

    private static string RowTypeIdentity(INamedTypeSymbol entity)
        => entity.ContainingType is not null ? RowTypeIdentity(entity.ContainingType) + "+" + entity.MetadataName :
            entity.ContainingAssembly.Name + ":" + NamespaceIdentity(entity.ContainingNamespace) + entity.MetadataName;

    private static string NamespaceIdentity(INamespaceSymbol value)
        => value.IsGlobalNamespace ? string.Empty : NamespaceIdentity(value.ContainingNamespace) + value.Name + ".";

    private static string DatabaseTypeName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }
        return type.SpecialType switch
        {
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_SByte => "Int8",
            SpecialType.System_Byte or SpecialType.System_Int16 => "Int16",
            SpecialType.System_Int32 => "Int32",
            SpecialType.System_Int64 => "Int64",
            SpecialType.System_Single => "Float32",
            SpecialType.System_Double => "Float64",
            SpecialType.System_Decimal => "Decimal",
            SpecialType.System_String => "String",
            _ => type is IArrayTypeSymbol ? "Binary" : type.Name switch
            {
                "DateOnly" => "Date", "TimeOnly" => "Time", _ => type.Name
            }
        };
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private sealed class Callback(SyntaxNode body, IParameterSymbol parameter, SemanticModel model)
    {
        internal SyntaxNode Body { get; } = body;
        internal IParameterSymbol Parameter { get; } = parameter;
        internal SemanticModel Model { get; } = model;
    }

    private sealed class SchemaResult
    {
        internal bool HasSqlAdapter { get; set; }
        internal HashSet<string> CustomTypes { get; } = new(StringComparer.Ordinal);
        internal List<Table> Tables { get; } = new();
        internal List<Diagnostic> Diagnostics { get; } = new();
        internal void Error(DiagnosticDescriptor descriptor, SyntaxNode node, string message)
            => Diagnostics.Add(Diagnostic.Create(descriptor, node.GetLocation(), message));
    }

    private sealed class Column(string name, ITypeSymbol type)
    {
        internal string Name { get; } = name;
        internal ITypeSymbol Type { get; } = type;
        internal string TypeName { get; } = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
        internal bool IsBinary { get; } = type is IArrayTypeSymbol;
        internal bool IsByte { get; } = type.SpecialType == SpecialType.System_Byte ||
            type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
            nullable.TypeArguments[0].SpecialType == SpecialType.System_Byte;
        internal bool IsDateTime { get; } = ScalarName(type) == "System.DateTime";
        internal bool IsDateTimeOffset { get; } = ScalarName(type) == "System.DateTimeOffset";

        private static string ScalarName(ITypeSymbol symbol)
            => (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0] : symbol).ToDisplayString();
    }

    private sealed class Reference(Column column, string targetEntity, Location location)
    {
        internal Column Column { get; } = column;
        internal string TargetEntity { get; } = targetEntity;
        internal Location Location { get; } = location;
        internal string TargetTable { get; set; } = string.Empty;
        internal string TargetKey { get; set; } = string.Empty;
    }

    private sealed class Table(INamedTypeSymbol entity, string name, List<Column> columns, Column key,
        List<Reference> references, HashSet<string> indexes, bool hasSqlAdapter, Location location)
    {
        internal string Name { get; } = name;
        internal bool HasSqlAdapter { get; } = hasSqlAdapter;
        internal INamedTypeSymbol Entity { get; } = entity;
        internal string EntityName { get; } = entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        internal List<Column> Columns { get; } = columns;
        internal Column Key { get; } = key;
        internal List<Reference> References { get; } = references;
        internal HashSet<string> Indexes { get; } = indexes;
        internal Location Location { get; } = location;
        internal string Signature => Literal(Name) + "|" + Literal(Key.Name) + "|" + string.Join("|", Columns.Select(column => Literal(column.Name))) +
            "|indexes:" + string.Join("|", Indexes.OrderBy(value => value, StringComparer.Ordinal).Select(Literal)) + "|references:" +
            string.Join("|", References.Select(reference => Literal(reference.Column.Name) + ":" + Literal(reference.TargetEntity) + ":" + Literal(reference.TargetTable) + ":" + Literal(reference.TargetKey))
                .OrderBy(value => value, StringComparer.Ordinal));
        internal bool IsPublic { get; } = Public(entity);
        internal string MapperName { get; } = MapperNamePrefix(entity) + "Mapper";

        private static string MapperNamePrefix(INamedTypeSymbol symbol)
            => symbol.ContainingType is null ? symbol.Name : MapperNamePrefix(symbol.ContainingType) + "_" + symbol.Name;

        private static bool Public(INamedTypeSymbol symbol)
            => symbol.DeclaredAccessibility == Accessibility.Public && (symbol.ContainingType is null || Public(symbol.ContainingType));
    }
}
