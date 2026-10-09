using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>
/// Validates and lowers retained C# schema declarations into stable compiled schemas. Internal
/// since phase 4 of the concrete-types plan (§6.7): the public entry points are
/// <see cref="SqlSchema.Compile()"/> and <see cref="SqlSchema.Compile(string, Action{SqlSchemaBuilder})"/>.
/// </summary>
internal static class SqlSchemaCompiler
{
    /// <summary>Compiles a schema declaration.</summary>
    /// <param name="declaration">The retained C# declaration.</param>
    /// <returns>An immutable compiled schema with a deterministic content hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="declaration"/> is null.</exception>
    /// <exception cref="SqlSchemaValidationException">The declaration cannot be represented in a SQL compiled schema.</exception>
    internal static SqlCompiledSchema Compile(SqlSchema declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        SqlSchemaDeclaration schema = declaration.Declaration;
        var errors = new List<SqlSchemaValidationError>();

        var customTypes = CompileTypes(schema, errors);
        var tables = CompileTables(schema, customTypes, errors);
        var principals = CompilePrincipals(schema, tables, errors);

        if (errors.Count > 0)
        {
            throw new SqlSchemaValidationException(errors);
        }

        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            schema.Name,
            schema.AllowsDestructiveChanges,
            customTypes.Values.OrderBy(type => type.Name, StringComparer.Ordinal).ToArray(),
            tables.OrderBy(table => table.Name, StringComparer.Ordinal).ToArray(),
            principals.OrderBy(principal => principal.Name, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<Type, CompiledSchemaType> CompileTypes(
        SqlSchemaDeclaration schema,
        List<SqlSchemaValidationError> errors)
    {
        var result = new Dictionary<Type, CompiledSchemaType>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SqlSchemaType declaration in schema.Types)
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
        SqlSchemaDeclaration schema,
        IReadOnlyDictionary<Type, CompiledSchemaType> customTypes,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaTable>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<Type, SqlSchemaTable>();

        foreach (SqlSchemaTable table in schema.Tables)
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

        foreach (SqlSchemaTable table in schema.Tables)
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
            foreach (SqlSchemaReference reference in table.References.OrderBy(value => value.Member, StringComparer.Ordinal))
            {
                if (!rows.TryGetValue(reference.TargetType, out SqlSchemaTable? target))
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
                        SqlSchemaValidationErrorCode.UnsupportedType,
                        $"{table.Name}.{source.Name}",
                        $"The reference type does not match '{target.Name}.{referenced.Name}'."));
                }
            }
        }
    }

    private static IReadOnlyList<CompiledSchemaColumn> CompileColumns(
        string owner,
        IReadOnlyList<SqlSchemaColumn> declarations,
        IReadOnlyDictionary<Type, CompiledSchemaType> customTypes,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaColumn>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SqlSchemaColumn column in declarations)
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

    private static List<CompiledSchemaPrincipal> CompilePrincipals(
        SqlSchemaDeclaration schema,
        IReadOnlyList<CompiledSchemaTable> tables,
        List<SqlSchemaValidationError> errors)
    {
        var result = new List<CompiledSchemaPrincipal>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new HashSet<string>(tables.Select(table => table.Name), StringComparer.OrdinalIgnoreCase);

        foreach (SqlSchemaPrincipal principal in schema.Principals)
        {
            if (!names.Add(principal.Name))
            {
                errors.Add(Error(SqlSchemaValidationErrorCode.DuplicateDeclaration, principal.Name, "The principal name is declared more than once."));
                continue;
            }

            var grants = new List<CompiledSchemaGrant>();
            foreach (IGrouping<SqlPermission, SqlSchemaGrant> permissionGroup in principal.Grants
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
}
