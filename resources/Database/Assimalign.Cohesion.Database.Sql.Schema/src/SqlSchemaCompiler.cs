using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Validates and lowers retained C# schema declarations into stable compiled schemas.</summary>
public static class SqlSchemaCompiler
{
    /// <summary>Compiles a schema declaration for an engine model.</summary>
    /// <param name="schema">The retained C# declaration.</param>
    /// <param name="model">The declared engine model.</param>
    /// <returns>An immutable compiled schema with a deterministic content hash.</returns>
    /// <exception cref="SqlSchemaValidationException">The declaration cannot be represented by the model.</exception>
    public static SqlCompiledSchema Compile(ISqlSchema schema, EngineModel model = EngineModel.Sql)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new List<SqlSchemaValidationError>();

        if (model != EngineModel.Sql)
        {
            errors.Add(Error(
                SqlSchemaValidationErrorCode.ModelMismatch,
                schema.Name,
                "A SQL compiled schema must target the SQL engine model."));
        }

        var customTypes = CompileTypes(schema, errors);
        var tables = CompileTables(schema, model, customTypes, errors);
        var functions = CompileFunctions(schema, customTypes, errors);
        var triggers = CompileTriggers(schema, tables, errors);
        var principals = CompilePrincipals(schema, tables, functions, errors);
        var extensions = CompileExtensions(schema, errors);

        if (errors.Count > 0)
        {
            throw new SqlSchemaValidationException(errors);
        }

        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            schema.Name,
            model,
            schema.AllowsDestructiveChanges,
            customTypes.Values.OrderBy(type => type.Name, StringComparer.Ordinal).ToArray(),
            tables.OrderBy(table => table.Name, StringComparer.Ordinal).ToArray(),
            functions.OrderBy(function => function.Name, StringComparer.Ordinal).ToArray(),
            triggers.OrderBy(trigger => trigger.Name, StringComparer.Ordinal).ToArray(),
            principals.OrderBy(principal => principal.Name, StringComparer.Ordinal).ToArray(),
            extensions.OrderBy(extension => extension.Name, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<Type, CompiledSchemaType> CompileTypes(
        ISqlSchema schema,
        List<SqlSchemaValidationError> errors)
    {
        var result = new Dictionary<Type, CompiledSchemaType>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ISqlSchemaType declaration in schema.Types)
        {
            string name = TypeId(declaration.ClrType);
            if (!names.Add(name) || result.ContainsKey(declaration.ClrType))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, name, "The custom type is declared more than once."));
                continue;
            }

            if (declaration.Precision is null || declaration.Scale is null)
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.IncompleteDeclaration, name, "A custom type must declare its storage representation."));
                continue;
            }

            result.Add(declaration.ClrType, new CompiledSchemaType(
                name,
                DatabaseType.Decimal,
                declaration.Precision,
                declaration.Scale));
        }

        return result;
    }

    private static List<CompiledSchemaTable> CompileTables(
        ISqlSchema schema,
        EngineModel model,
        IReadOnlyDictionary<Type, CompiledSchemaType> customTypes,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaTable>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<Type, ISqlSchemaTable>();

        foreach (ISqlSchemaTable table in schema.Tables)
        {
            if (!names.Add(table.Name))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, table.Name, "The table name is declared more than once."));
                continue;
            }

            if (!rows.TryAdd(table.RowType, table))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, table.Name, $"CLR row type '{TypeId(table.RowType)}' is mapped more than once."));
            }
        }

        foreach (ISqlSchemaTable table in schema.Tables)
        {
            if (!names.Contains(table.Name) || result.Any(item => string.Equals(item.Name, table.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            IReadOnlyList<CompiledSchemaColumn> columns = CompileColumns(table.Name, table.ColumnDefinitions, customTypes, errors);
            var columnNames = new HashSet<string>(columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
            CompiledSchemaKey? primaryKey = null;
            if (table.PrimaryKey is not null)
            {
                if (!columnNames.Contains(table.PrimaryKey))
                {
                    errors.Add(Error(SqlSchemaValidationErrorCode.UnknownReference, $"{table.Name}.{table.PrimaryKey}", "The primary-key column is not declared."));
                }
                else
                {
                    primaryKey = new CompiledSchemaKey($"PK_{table.Name}", Array.AsReadOnly([table.PrimaryKey]));
                }
            }

            var indexes = new List<CompiledSchemaIndex>();
            var indexMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string member in table.Indexes.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (!columnNames.Contains(member))
                {
                    errors.Add(Error(SqlSchemaValidationErrorCode.UnknownReference, $"{table.Name}.{member}", "The index column is not declared."));
                }
                else if (!indexMembers.Add(member))
                {
                    errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, $"{table.Name}.{member}", "The index is declared more than once."));
                }
                else
                {
                    indexes.Add(new CompiledSchemaIndex($"IX_{table.Name}_{member}", Array.AsReadOnly([member])));
                }
            }

            var constraints = new List<CompiledSchemaConstraint>();
            var constraintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ISqlSchemaReference reference in table.References.OrderBy(value => value.Member, StringComparer.Ordinal))
            {
                if (!rows.TryGetValue(reference.TargetType, out ISqlSchemaTable? target))
                {
                    errors.Add(Error(SqlSchemaValidationErrorCode.UnknownReference, $"{table.Name}.{reference.Member}", $"Referenced row type '{TypeId(reference.TargetType)}' is not declared."));
                    continue;
                }

                if (!columnNames.Contains(reference.Member))
                {
                    errors.Add(Error(SqlSchemaValidationErrorCode.UnknownReference, $"{table.Name}.{reference.Member}", "The reference column is not declared."));
                    continue;
                }

                if (target.PrimaryKey is null)
                {
                    errors.Add(Error(SqlSchemaValidationErrorCode.IncompleteDeclaration, target.Name, "A referenced table must declare a primary key."));
                    continue;
                }

                string constraintName = $"FK_{table.Name}_{target.Name}_{reference.Member}";
                if (!constraintNames.Add(constraintName))
                {
                    errors.Add(Error(
                        SqlSchemaValidationErrorCode.DuplicateDeclaration,
                        constraintName,
                        "The reference constraint is declared more than once."));
                    continue;
                }

                constraints.Add(new CompiledSchemaConstraint(
                    constraintName,
                    CompiledSchemaConstraintKind.Reference,
                    Array.AsReadOnly([reference.Member]),
                    target.Name,
                    Array.AsReadOnly([target.PrimaryKey])));
            }

            result.Add(new CompiledSchemaTable(
                table.Name,
                TypeId(table.RowType),
                columns,
                primaryKey,
                indexes,
                constraints));
        }

        ValidateReferenceTypes(result, errors);
        return result;
    }

    private static void ValidateReferenceTypes(
        IReadOnlyList<CompiledSchemaTable> tables,
        List<SqlSchemaValidationError> errors)
    {
        var tableMap = tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        foreach (CompiledSchemaTable table in tables)
        {
            var sourceColumns = table.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            foreach (CompiledSchemaConstraint constraint in table.Constraints)
            {
                if (constraint.Kind != CompiledSchemaConstraintKind.Reference ||
                    constraint.ReferencedObject is null ||
                    !tableMap.TryGetValue(constraint.ReferencedObject, out CompiledSchemaTable? target) ||
                    constraint.Columns.Count != 1 ||
                    constraint.ReferencedColumns.Count != 1 ||
                    !sourceColumns.TryGetValue(constraint.Columns[0], out CompiledSchemaColumn? source))
                {
                    continue;
                }

                CompiledSchemaColumn? referenced = target.Columns.FirstOrDefault(column =>
                    string.Equals(column.Name, constraint.ReferencedColumns[0], StringComparison.OrdinalIgnoreCase));
                if (referenced is null)
                {
                    continue;
                }

                if (source.Type != referenced.Type ||
                    !string.Equals(source.CustomType, referenced.CustomType, StringComparison.Ordinal) ||
                    source.MaxLength != referenced.MaxLength ||
                    source.Precision != referenced.Precision ||
                    source.Scale != referenced.Scale)
                {
                    errors.Add(Error(
                        SqlSchemaValidationErrorCode.ModelMismatch,
                        $"{table.Name}.{source.Name}",
                        $"The reference type does not match '{target.Name}.{referenced.Name}'."));
                }
            }
        }
    }

    private static IReadOnlyList<CompiledSchemaColumn> CompileColumns(
        string owner,
        IReadOnlyList<ISqlSchemaColumn> declarations,
        IReadOnlyDictionary<Type, CompiledSchemaType> customTypes,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaColumn>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ISqlSchemaColumn column in declarations)
        {
            if (!names.Add(column.Name))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, $"{owner}.{column.Name}", "The column is declared more than once."));
                continue;
            }

            if (!TryResolveType(column.ClrType, customTypes, out DatabaseType type, out string? custom, out int? precision, out int? scale))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.UnsupportedType, $"{owner}.{column.Name}", $"CLR type '{TypeId(column.ClrType)}' is not supported."));
                continue;
            }

            result.Add(new CompiledSchemaColumn(column.Name, type, column.IsNullable, Precision: precision, Scale: scale, CustomType: custom));
        }

        if (result.Count == 0)
        {
            errors.Add(Error(SqlSchemaValidationErrorCode.IncompleteDeclaration, owner, "A table must declare at least one column."));
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static List<CompiledSchemaFunction> CompileFunctions(
        ISqlSchema schema,
        IReadOnlyDictionary<Type, CompiledSchemaType> customTypes,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaFunction>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ISqlSchemaFunction function in schema.Functions)
        {
            if (!names.Add(function.Name))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, function.Name, "The function name is declared more than once."));
                continue;
            }

            if (!TryResolveType(function.Body.ReturnType, customTypes, out DatabaseType resultType, out string? customResult, out _, out _))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.UnsupportedType, function.Name, $"Function result type '{TypeId(function.Body.ReturnType)}' is not supported."));
                continue;
            }

            var parameters = new List<CompiledSchemaParameter>();
            bool invalidParameter = false;
            foreach (ParameterExpression parameter in function.Body.Parameters)
            {
                if (!TryResolveType(parameter.Type, customTypes, out DatabaseType parameterType, out string? customParameter, out _, out _))
                {
                    errors.Add(Error(SqlSchemaValidationErrorCode.UnsupportedType, function.Name, $"Function parameter type '{TypeId(parameter.Type)}' is not supported."));
                    invalidParameter = true;
                    continue;
                }

                parameters.Add(new CompiledSchemaParameter($"arg{parameters.Count}", parameterType, customParameter));
            }

            if (invalidParameter || !TryCompileExpression(function.Name, function.Body, errors, out CompiledSchemaExpression? expression))
            {
                continue;
            }

            result.Add(new CompiledSchemaFunction(function.Name, parameters.AsReadOnly(), resultType, customResult, expression!));
        }

        return result;
    }

    private static List<CompiledSchemaTrigger> CompileTriggers(
        ISqlSchema schema,
        IReadOnlyList<CompiledSchemaTable> tables,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaTrigger>();
        var rawTables = new Dictionary<Type, string>();
        foreach (ISqlSchemaTable table in schema.Tables)
        {
            rawTables.TryAdd(table.RowType, table.Name);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ISqlSchemaTrigger trigger in schema.Triggers)
        {
            if (!rawTables.TryGetValue(trigger.RowType, out string? tableName) || !tables.Any(table => string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase)))
            {
                string declaration = $"{TypeId(trigger.RowType)}.{trigger.Event}";
                errors.Add(Error(SqlSchemaValidationErrorCode.UnknownReference, declaration, "The trigger target table is not declared."));
                continue;
            }

            string name = $"TR_{tableName}_{trigger.Event}";
            if (!names.Add(name))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, name, "The trigger is declared more than once."));
                continue;
            }

            if (TryCompileExpression(name, trigger.Body, errors, out CompiledSchemaExpression? expression))
            {
                result.Add(new CompiledSchemaTrigger(name, tableName, trigger.Event, expression!));
            }
        }

        return result;
    }

    private static List<CompiledSchemaPrincipal> CompilePrincipals(
        ISqlSchema schema,
        IReadOnlyList<CompiledSchemaTable> tables,
        IReadOnlyList<CompiledSchemaFunction> functions,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaPrincipal>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new HashSet<string>(tables.Select(table => table.Name), StringComparer.OrdinalIgnoreCase);
        objects.UnionWith(functions.Select(function => function.Name));

        foreach (ISqlSchemaPrincipal principal in schema.Principals)
        {
            if (!names.Add(principal.Name))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, principal.Name, "The principal name is declared more than once."));
                continue;
            }

            var grants = new List<CompiledSchemaGrant>();
            foreach (IGrouping<SqlPermission, ISqlSchemaGrant> permissionGroup in principal.Grants
                .GroupBy(value => value.Permission)
                .OrderBy(value => value.Key))
            {
                string[] grantedObjects = permissionGroup
                    .SelectMany(grant => grant.Objects)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                foreach (string grantedObject in grantedObjects)
                {
                    if (!objects.Contains(grantedObject))
                    {
                        errors.Add(Error(SqlSchemaValidationErrorCode.UnknownReference, $"{principal.Name}.{grantedObject}", "The granted schema object is not declared."));
                    }
                }

                grants.Add(new CompiledSchemaGrant(permissionGroup.Key, Array.AsReadOnly(grantedObjects)));
            }

            result.Add(new CompiledSchemaPrincipal(principal.Name, grants.AsReadOnly()));
        }

        return result;
    }

    private static List<CompiledSchemaExtension> CompileExtensions(
        ISqlSchema schema,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaExtension>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ISqlSchemaExtension extension in schema.Extensions)
        {
            if (!names.Add(extension.Name))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, extension.Name, "The extension name is declared more than once."));
                continue;
            }

            result.Add(new CompiledSchemaExtension(extension.Name, extension.Value));
        }

        return result;
    }

    private static bool TryCompileExpression(
        string declaration,
        LambdaExpression expression,
        List<SqlSchemaValidationError> errors,
        out CompiledSchemaExpression? compiled)
    {
        try
        {
            compiled = new CompiledSchemaExpression(ExpressionCanonicalizer.Canonicalize(expression));
            return true;
        }
        catch (NotSupportedException exception)
        {
            errors.Add(Error(SqlSchemaValidationErrorCode.UnsupportedExpression, declaration, exception.Message));
            compiled = null;
            return false;
        }
    }

    private static bool TryResolveType(
        Type declaredType,
        IReadOnlyDictionary<Type, CompiledSchemaType> customTypes,
        out DatabaseType type,
        out string? customType,
        out int? precision,
        out int? scale)
    {
        Type valueType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (customTypes.TryGetValue(valueType, out CompiledSchemaType? custom))
        {
            type = custom.StorageType;
            customType = custom.Name;
            precision = custom.Precision;
            scale = custom.Scale;
            return true;
        }

        customType = null;
        precision = null;
        scale = null;
        type = valueType == typeof(bool) ? DatabaseType.Boolean
            : valueType == typeof(sbyte) ? DatabaseType.Int8
            : valueType == typeof(byte) || valueType == typeof(short) ? DatabaseType.Int16
            : valueType == typeof(int) ? DatabaseType.Int32
            : valueType == typeof(long) ? DatabaseType.Int64
            : valueType == typeof(float) ? DatabaseType.Float32
            : valueType == typeof(double) ? DatabaseType.Float64
            : valueType == typeof(decimal) ? DatabaseType.Decimal
            : valueType == typeof(string) ? DatabaseType.String
            : valueType == typeof(byte[]) ? DatabaseType.Binary
            : valueType == typeof(DateOnly) ? DatabaseType.Date
            : valueType == typeof(TimeOnly) ? DatabaseType.Time
            : valueType == typeof(DateTime) ? DatabaseType.DateTime
            : valueType == typeof(DateTimeOffset) ? DatabaseType.DateTimeOffset
            : valueType == typeof(TimeSpan) ? DatabaseType.TimeSpan
            : valueType == typeof(Guid) ? DatabaseType.Guid
            : DatabaseType.Null;
        return type != DatabaseType.Null;
    }

    private static SqlSchemaValidationError Error(
        SqlSchemaValidationErrorCode code,
        string declaration,
        string message)
        => new(code, declaration, message);

    private static string TypeId(Type type)
    {
        if (type.IsGenericParameter)
        {
            return $"!{type.GenericParameterPosition}:{type.Name}";
        }

        // The statically supplied identity already contains generic arguments and array
        // suffixes. Read that string without discovering types or members through reflection.
        return CanonicalTypeIdentity(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
    }

    private static string CanonicalTypeIdentity(ReadOnlySpan<char> identity)
    {
        int depth = 0;
        int assemblySeparator = -1;
        for (int index = 0; index < identity.Length; index++)
        {
            char character = identity[index];
            if (character == '[')
            {
                depth++;
            }
            else if (character == ']')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                assemblySeparator = index;
                break;
            }
        }

        ReadOnlySpan<char> typeName = assemblySeparator < 0 ? identity : identity[..assemblySeparator];
        ReadOnlySpan<char> assembly = assemblySeparator < 0 ? "unknown" : identity[(assemblySeparator + 1)..].Trim();
        int qualifier = assembly.IndexOf(',');
        if (qualifier >= 0)
        {
            assembly = assembly[..qualifier];
        }

        int genericStart = typeName.IndexOf("[[", StringComparison.Ordinal);
        if (genericStart < 0)
        {
            return $"{assembly}:{typeName}";
        }

        var result = new StringBuilder().Append(assembly).Append(':').Append(typeName[..genericStart]).Append('[');
        int position = genericStart + 1;
        while (typeName[position] == '[')
        {
            int argumentStart = ++position;
            depth = 1;
            while (depth > 0)
            {
                char character = typeName[position++];
                if (character == '[')
                {
                    depth++;
                }
                else if (character == ']')
                {
                    depth--;
                }
            }

            result.Append(CanonicalTypeIdentity(typeName[argumentStart..(position - 1)]));
            if (typeName[position] != ',')
            {
                break;
            }
            result.Append(';');
            position++;
        }

        // Keep array rank, jagged-array, pointer, and by-reference suffixes verbatim.
        return result.Append(']').Append(typeName[(position + 1)..]).ToString();
    }

    private sealed class ExpressionCanonicalizer
    {
        private readonly StringBuilder _builder = new();
        private readonly Dictionary<ParameterExpression, string> _parameters = new();

        internal static string Canonicalize(LambdaExpression expression)
        {
            var writer = new ExpressionCanonicalizer();
            writer.Write(expression);
            return writer._builder.ToString();
        }

        private void Write(Expression expression)
        {
            switch (expression)
            {
                case LambdaExpression lambda:
                    _builder.Append("lambda<").Append(TypeId(lambda.ReturnType)).Append(">(");
                    for (int index = 0; index < lambda.Parameters.Count; index++)
                    {
                        if (index > 0)
                        {
                            _builder.Append(',');
                        }

                        string parameterId = $"p{_parameters.Count}";
                        _parameters[lambda.Parameters[index]] = parameterId;
                        _builder.Append(parameterId).Append(':').Append(TypeId(lambda.Parameters[index].Type));
                    }
                    _builder.Append(")->");
                    Write(lambda.Body);
                    break;
                case ParameterExpression parameter:
                    _builder.Append(_parameters.TryGetValue(parameter, out string? id) ? id : throw Unsupported(expression));
                    break;
                case ConstantExpression constant:
                    WriteConstant(constant);
                    break;
                case MemberExpression member:
                    if (member.Expression is null)
                    {
                        throw Unsupported(expression);
                    }

                    _builder.Append("member<")
                        .Append(TypeId(member.Type))
                        .Append(">(")
                        .Append(TypeId(member.Member.DeclaringType!))
                        .Append('.')
                        .Append(member.Member.Name)
                        .Append(',');
                    Write(member.Expression);
                    _builder.Append(')');
                    break;
                case UnaryExpression unary when unary.NodeType is ExpressionType.Convert or ExpressionType.Negate or ExpressionType.Not or ExpressionType.Quote:
                    if (unary.Method is not null && !IsAllowedMethod(unary.Method.DeclaringType))
                    {
                        throw Unsupported(expression);
                    }

                    _builder.Append(unary.NodeType)
                        .Append('<').Append(TypeId(unary.Type)).Append('>')
                        .Append("[method=").Append(MethodId(
                            unary.Method?.DeclaringType, unary.Method?.Name, unary.Type, [unary.Operand])).Append("](");
                    Write(unary.Operand);
                    _builder.Append(')');
                    break;
                case BinaryExpression binary:
                    if (binary.Method is not null && !IsAllowedMethod(binary.Method.DeclaringType))
                    {
                        throw Unsupported(expression);
                    }

                    _builder.Append(binary.NodeType)
                        .Append('<').Append(TypeId(binary.Type)).Append('>')
                        .Append("[method=").Append(MethodId(
                            binary.Method?.DeclaringType, binary.Method?.Name, binary.Type, [binary.Left, binary.Right]))
                        .Append(";lifted=").Append(binary.IsLifted ? '1' : '0')
                        .Append(";liftedToNull=").Append(binary.IsLiftedToNull ? '1' : '0')
                        .Append("](");
                    Write(binary.Left);
                    _builder.Append(',');
                    Write(binary.Right);
                    if (binary.Conversion is not null)
                    {
                        _builder.Append(',');
                        Write(binary.Conversion);
                    }
                    _builder.Append(')');
                    break;
                case MethodCallExpression call:
                    if (!IsAllowedMethod(call.Method.DeclaringType))
                    {
                        throw Unsupported(expression);
                    }

                    _builder.Append("call(").Append(MethodId(
                        call.Method.DeclaringType, call.Method.Name, call.Type, call.Arguments));
                    _builder.Append(',');
                    if (call.Object is null)
                    {
                        _builder.Append("static");
                    }
                    else
                    {
                        Write(call.Object);
                    }
                    foreach (Expression argument in call.Arguments)
                    {
                        _builder.Append(',');
                        Write(argument);
                    }
                    _builder.Append(')');
                    break;
                case ConditionalExpression conditional:
                    _builder.Append("conditional(");
                    Write(conditional.Test);
                    _builder.Append(',');
                    Write(conditional.IfTrue);
                    _builder.Append(',');
                    Write(conditional.IfFalse);
                    _builder.Append(')');
                    break;
                case NewExpression created:
                    throw Unsupported(created);
                default:
                    throw Unsupported(expression);
            }
        }

        private void WriteConstant(ConstantExpression constant)
        {
            object? value = constant.Value;
            if (value is null)
            {
                _builder.Append("null:").Append(TypeId(constant.Type));
                return;
            }

            if (value is string text)
            {
                _builder.Append("string:").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
                return;
            }

            if (value is char character)
            {
                _builder.Append("char:").Append(((int)character).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is bool boolean)
            {
                _builder.Append(boolean ? "bool:true" : "bool:false");
                return;
            }

            if (value.GetType().IsEnum)
            {
                _builder.Append("enum:").Append(TypeId(value.GetType())).Append(':').Append(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is IFormattable formattable && value is not DateTime && value is not DateTimeOffset)
            {
                _builder.Append("value:").Append(TypeId(value.GetType())).Append(':').Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            throw Unsupported(constant);
        }

        private static bool IsAllowedMethod(Type? declaringType)
        {
            return declaringType == typeof(ISqlTriggerContext)
                || declaringType == typeof(string)
                || declaringType == typeof(Math)
                || declaringType == typeof(MathF)
                || declaringType == typeof(decimal)
                || declaringType == typeof(Convert);
        }

        private static string MethodId(
            Type? declaringType,
            string? methodName,
            Type resultType,
            IReadOnlyList<Expression> arguments)
        {
            if (declaringType is null)
            {
                return "none";
            }

            var builder = new StringBuilder()
                .Append(TypeId(declaringType))
                .Append('.')
                .Append(methodName);

            // The caller already supplied the complete typed expression. Its argument and
            // result nodes identify the signature without reflection-based member discovery.
            builder.Append('(')
                .AppendJoin(',', arguments.Select(argument => TypeId(argument.Type)))
                .Append(")->")
                .Append(TypeId(resultType));
            return builder.ToString();
        }

        private static NotSupportedException Unsupported(Expression expression)
            => new($"Expression node '{expression.NodeType}' with CLR type '{TypeId(expression.Type)}' is not deterministic schema syntax.");
    }
}
