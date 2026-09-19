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

        var result = new SchemaResult();
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
        }

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

        if (!TryCallback(Argument(invocation, method, "configure"), model.Compilation, token, out Callback? callback) ||
            !TryStatements(callback!, out IReadOnlyList<InvocationExpressionSyntax> calls))
        {
            result.Error(SqlMapperDiagnostics.UnsupportedDeclaration, invocation,
                "Table configuration must be a source-declared callback containing only direct builder calls.");
            return;
        }

        var columns = new List<Column>();
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
            if (member!.Name is "Snapshot" or "Matches" or "BytesEqual" || member.Name.StartsWith("_value", StringComparison.Ordinal))
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
                column = new Column(member!.Name, type);
                columns.Add(column);
            }

            if (called.Name == "Key" || called.Name == "PrimaryKey")
            {
                key = column; // Matches the retained builder's last primary-key declaration.
            }
        }

        if (key is null || key.IsBinary || IsNullable(key.Type))
        {
            result.Error(SqlMapperDiagnostics.InvalidKey, invocation,
                "A generated mapper requires one declared non-null immutable scalar primary key; byte[] and nullable keys are unsupported.");
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
            result.Tables.Add(new Table(entity, tableName, columns, key!, invocation.GetLocation()));
        }
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

            foreach (Table table in result.Tables)
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
                        "Entity '" + table.EntityName + "' has conflicting table, member-order or primary-key declarations."));
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
        text.Append(table.IsPublic ? "public" : "internal").Append(" sealed class ").Append(mapper)
            .Append(" : ").Append(MappingNamespace).Append("IEntityMapper<").Append(entity).Append(", ").Append(key).Append(", ")
            .Append(mapper).Append(".Snapshot>, ").Append(MappingNamespace).Append("IEntityReader<").Append(entity)
            .Append(", global::System.Collections.Generic.IReadOnlyList<object?>>, ").Append(MappingNamespace)
            .Append("IEntityWriter<").Append(entity).AppendLine(", global::System.Collections.Generic.IList<object?>>\n{");
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

    private sealed class Callback(SyntaxNode body, IParameterSymbol parameter, SemanticModel model)
    {
        internal SyntaxNode Body { get; } = body;
        internal IParameterSymbol Parameter { get; } = parameter;
        internal SemanticModel Model { get; } = model;
    }

    private sealed class SchemaResult
    {
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

    private sealed class Table(INamedTypeSymbol entity, string name, List<Column> columns, Column key, Location location)
    {
        internal INamedTypeSymbol Entity { get; } = entity;
        internal string EntityName { get; } = entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        internal List<Column> Columns { get; } = columns;
        internal Column Key { get; } = key;
        internal Location Location { get; } = location;
        internal string Signature { get; } = name + "|" + key.Name + "|" + string.Join("|", columns.Select(column => column.Name));
        internal bool IsPublic { get; } = Public(entity);
        internal string MapperName { get; } = Name(entity) + "Mapper";

        private static string Name(INamedTypeSymbol symbol)
            => symbol.ContainingType is null ? symbol.Name : Name(symbol.ContainingType) + "_" + symbol.Name;

        private static bool Public(INamedTypeSymbol symbol)
            => symbol.DeclaredAccessibility == Accessibility.Public && (symbol.ContainingType is null || Public(symbol.ContainingType));
    }
}
