using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>
/// Validates the portable compiled-schema contract at every trust boundary.
/// </summary>
internal static class SqlCompiledSchemaValidator
{
    internal static void Validate(SqlCompiledSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        ValidateFormat(schema.Format);
        var errors = new List<SqlSchemaValidationError>();

        RequireName(schema.Name, "schema.name", errors);

        Dictionary<string, CompiledSchemaType> types = ValidateTypes(schema.Types, errors);
        Dictionary<string, CompiledSchemaTable> tables = CollectTables(schema.Tables, errors);
        Dictionary<CompiledSchemaTable, Dictionary<string, CompiledSchemaColumn>> tableColumns =
            ValidateTables(schema.Tables, types, errors);
        ValidateTableConstraints(schema.Tables, tables, tableColumns, errors);
        ValidatePrincipals(schema.Principals, tables, errors);

        if (errors.Count > 0)
        {
            throw new SqlSchemaValidationException(errors);
        }
    }

    internal static void ValidateFormat(string? format)
    {
        if (!string.Equals(format, SqlCompiledSchema.CurrentFormat, StringComparison.Ordinal))
        {
            throw InvalidDocument("schema.format",
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaValidatorValidateFormatFailure, format, SqlCompiledSchema.CurrentFormat));
        }
    }

    internal static SqlSchemaValidationException InvalidDocument(
        string declaration,
        string message,
        Exception? innerException = null)
        => new([
            new SqlSchemaValidationError(
                SqlSchemaValidationErrorCode.InvalidDocument,
                declaration,
                message)], innerException);

    private static Dictionary<string, CompiledSchemaType> ValidateTypes(
        IReadOnlyList<CompiledSchemaType> declarations,
        List<SqlSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaType>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaType? declaration = declarations[index];
            string path = NamedPath("types", declaration?.Name, index);
            if (declaration is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, path,
                    ErrorMessages.SchemaValidatorValidateTypesVoid);
                continue;
            }

            if (!RequireName(declaration.Name, path, errors))
            {
                continue;
            }

            // A declared type is a domain of the SQL engine, named in DDL by this name.
            if (declaration.Name.ToUpperInvariant() is "SERIAL" or "BIGSERIAL" or "SMALLSERIAL")
            {
                Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, path,
                    SqlTypeModifiers.GetError(declaration.Name, null, null)!);
                continue;
            }
            if (!CompiledSchemaTypeNames.IsIdentifier(declaration.Name))
            {
                Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, path,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaValidatorValidateTypesVoid2, declaration.Name));
                continue;
            }

            if (!result.TryAdd(declaration.Name, declaration))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    ErrorMessages.SchemaValidatorValidateTypesVoid3);
            }

            ValidateStorageType(
                declaration.StorageType,
                null,
                declaration.Precision,
                declaration.Scale,
                path,
                errors);
        }

        return result;
    }

    private static Dictionary<string, CompiledSchemaTable> CollectTables(
        IReadOnlyList<CompiledSchemaTable> declarations,
        List<SqlSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaTable>(StringComparer.OrdinalIgnoreCase);
        var rowTypes = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaTable? table = declarations[index];
            string path = NamedPath("tables", table?.Name, index);
            if (table is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, path,
                    ErrorMessages.SchemaValidatorCollectTablesVoid);
                continue;
            }

            if (RequireName(table.Name, path, errors) && !result.TryAdd(table.Name, table))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    ErrorMessages.SchemaValidatorCollectTablesVoid2);
            }

            if (!RequireName(table.RowType, $"{path}.rowType", errors))
            {
                continue;
            }

            if (!rowTypes.Add(table.RowType))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, $"{path}.rowType",
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaValidatorCollectTablesVoid3, table.RowType));
            }
        }

        return result;
    }

    private static Dictionary<CompiledSchemaTable, Dictionary<string, CompiledSchemaColumn>> ValidateTables(
        IReadOnlyList<CompiledSchemaTable> declarations,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        List<SqlSchemaValidationError> errors)
    {
        var result = new Dictionary<CompiledSchemaTable, Dictionary<string, CompiledSchemaColumn>>();
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaTable? table = declarations[index];
            if (table is null)
            {
                continue;
            }

            string path = NamedPath("tables", table.Name, index);
            Dictionary<string, CompiledSchemaColumn> columns =
                ValidateColumns(table.Columns, types, $"{path}.columns", errors);
            result[table] = columns;

            if (table.Columns.Count == 0)
            {
                Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, path,
                    ErrorMessages.SchemaValidatorValidateTablesVoid);
            }

            if (table.PrimaryKey is not null)
            {
                ValidateKey(table.PrimaryKey, columns, $"{path}.primaryKey", errors);
            }

            ValidateIndexes(table.Indexes, columns, $"{path}.indexes", errors);
        }

        return result;
    }

    private static void ValidateTableConstraints(
        IReadOnlyList<CompiledSchemaTable> declarations,
        IReadOnlyDictionary<string, CompiledSchemaTable> tables,
        IReadOnlyDictionary<CompiledSchemaTable, Dictionary<string, CompiledSchemaColumn>> tableColumns,
        List<SqlSchemaValidationError> errors)
    {
        for (int tableIndex = 0; tableIndex < declarations.Count; tableIndex++)
        {
            CompiledSchemaTable? table = declarations[tableIndex];
            if (table is null || !tableColumns.TryGetValue(table, out Dictionary<string, CompiledSchemaColumn>? columns))
            {
                continue;
            }

            string tablePath = NamedPath("tables", table.Name, tableIndex);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < table.Constraints.Count; index++)
            {
                CompiledSchemaConstraint? constraint = table.Constraints[index];
                string path = NamedPath($"{tablePath}.constraints", constraint?.Name, index);
                if (constraint is null)
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, path,
                        ErrorMessages.ValidatorValidateTableConstraintsVoid);
                    continue;
                }

                if (RequireName(constraint.Name, path, errors) && !names.Add(constraint.Name))
                {
                    Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                        ErrorMessages.ValidatorValidateTableConstraintsVoid2);
                }

                if (!Enum.IsDefined(constraint.Kind))
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, $"{path}.kind",
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidatorValidateTableConstraintsVoid3, constraint.Kind));
                    continue;
                }

                if (!Enum.IsDefined(constraint.OnDelete) ||
                    (constraint.Kind == CompiledSchemaConstraintKind.Check && constraint.OnDelete != CompiledSchemaReferentialAction.Restrict))
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, $"{path}.onDelete",
                        ErrorMessages.ValidatorValidateTableConstraintsVoid4);
                }

                if (constraint.Kind != CompiledSchemaConstraintKind.Check || constraint.Columns.Count > 0)
                {
                    ValidateMemberNames(constraint.Columns, columns, $"{path}.columns", errors);
                }
                if (constraint.Kind == CompiledSchemaConstraintKind.Check)
                {
                    ValidateExpression(constraint.Expression, $"{path}.expression", errors);
                    continue;
                }

                if (!RequireName(constraint.ReferencedObject, $"{path}.referencedObject", errors) ||
                    !tables.TryGetValue(constraint.ReferencedObject!, out CompiledSchemaTable? target) ||
                    !tableColumns.TryGetValue(target, out Dictionary<string, CompiledSchemaColumn>? targetColumns))
                {
                    Add(errors, SqlSchemaValidationErrorCode.UnknownReference, $"{path}.referencedObject",
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidatorValidateTableConstraintsVoid5, constraint.ReferencedObject));
                    continue;
                }

                ValidateMemberNames(
                    constraint.ReferencedColumns,
                    targetColumns,
                    $"{path}.referencedColumns",
                    errors);
                if (constraint.Columns.Count != constraint.ReferencedColumns.Count)
                {
                    Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, path,
                        ErrorMessages.ValidatorValidateTableConstraintsVoid6);
                    continue;
                }

                for (int columnIndex = 0; columnIndex < constraint.Columns.Count; columnIndex++)
                {
                    if (columns.TryGetValue(constraint.Columns[columnIndex], out CompiledSchemaColumn? source) &&
                        targetColumns.TryGetValue(constraint.ReferencedColumns[columnIndex], out CompiledSchemaColumn? referenced) &&
                        !TypesEqual(source, referenced))
                    {
                        Add(errors, SqlSchemaValidationErrorCode.UnsupportedType,
                            $"{path}.columns.{constraint.Columns[columnIndex]}",
                            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidatorValidateTableConstraintsVoid7, target.Name, referenced.Name));
                    }
                }
            }
        }
    }

    private static void ValidatePrincipals(
        IReadOnlyList<CompiledSchemaPrincipal> declarations,
        IReadOnlyDictionary<string, CompiledSchemaTable> tables,
        List<SqlSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaPrincipal? principal = declarations[index];
            string path = NamedPath("principals", principal?.Name, index);
            if (principal is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, path,
                    ErrorMessages.SchemaValidatorValidatePrincipalsVoid);
                continue;
            }

            if (RequireName(principal.Name, path, errors) && !names.Add(principal.Name))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    ErrorMessages.SchemaValidatorValidatePrincipalsVoid2);
            }

            var grantedPermissions = new HashSet<SqlPermission>();
            for (int grantIndex = 0; grantIndex < principal.Grants.Count; grantIndex++)
            {
                CompiledSchemaGrant? grant = principal.Grants[grantIndex];
                string grantPath = $"{path}.grants[{grantIndex}]";
                if (grant is null)
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, grantPath,
                        ErrorMessages.SchemaValidatorValidatePrincipalsVoid3);
                    continue;
                }

                if (!Enum.IsDefined(grant.Permission))
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, $"{grantPath}.permission",
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaValidatorValidatePrincipalsVoid4, grant.Permission));
                }
                else if (!grantedPermissions.Add(grant.Permission))
                {
                    Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, grantPath,
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaValidatorValidatePrincipalsVoid5, grant.Permission));
                }

                if (grant.Objects.Count == 0)
                {
                    Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, grantPath,
                        ErrorMessages.SchemaValidatorValidatePrincipalsVoid6);
                }

                var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string? objectName in grant.Objects)
                {
                    string objectPath = $"{grantPath}.objects";
                    if (!RequireName(objectName, objectPath, errors))
                    {
                        continue;
                    }

                    if (!objects.Add(objectName!))
                    {
                        Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration,
                            $"{objectPath}.{objectName}", ErrorMessages.SchemaValidatorValidatePrincipalsVoid7);
                    }

                    if (!tables.ContainsKey(objectName!))
                    {
                        Add(errors, SqlSchemaValidationErrorCode.UnknownReference,
                            $"{objectPath}.{objectName}", ErrorMessages.SchemaValidatorValidatePrincipalsVoid8);
                    }
                }

            }
        }
    }

    private static Dictionary<string, CompiledSchemaColumn> ValidateColumns(
        IReadOnlyList<CompiledSchemaColumn> declarations,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaColumn>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaColumn? column = declarations[index];
            string columnPath = NamedPath(path, column?.Name, index);
            if (column is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, columnPath,
                    ErrorMessages.SchemaValidatorValidateColumnsVoid);
                continue;
            }

            if (RequireName(column.Name, columnPath, errors) && !result.TryAdd(column.Name, column))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, columnPath,
                    ErrorMessages.SchemaValidatorValidateColumnsVoid2);
            }

            ValidateTypeReference(column.Type, column.CustomType, types, $"{columnPath}.type", errors);
            ValidateStorageType(column.Type, column.MaxLength, column.Precision, column.Scale, columnPath, errors);
            if (column.DefaultLiteral is not null && string.IsNullOrWhiteSpace(column.DefaultLiteral))
            {
                Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, $"{columnPath}.defaultLiteral",
                    ErrorMessages.SchemaValidatorValidateColumnsVoid3);
            }
        }

        return result;
    }

    private static void ValidateKey(
        CompiledSchemaKey key,
        IReadOnlyDictionary<string, CompiledSchemaColumn> columns,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        RequireName(key.Name, $"{path}.name", errors);
        if (key.Columns.Count == 0)
        {
            Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, path,
                ErrorMessages.SchemaValidatorValidateKeyVoid);
        }

        ValidateMemberNames(key.Columns, columns, $"{path}.columns", errors);
    }

    private static void ValidateIndexes(
        IReadOnlyList<CompiledSchemaIndex> indexes,
        IReadOnlyDictionary<string, CompiledSchemaColumn> columns,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < indexes.Count; index++)
        {
            CompiledSchemaIndex? declaration = indexes[index];
            string indexPath = NamedPath(path, declaration?.Name, index);
            if (declaration is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, indexPath,
                    ErrorMessages.SchemaValidatorValidateIndexesVoid);
                continue;
            }

            if (RequireName(declaration.Name, indexPath, errors) && !names.Add(declaration.Name))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, indexPath,
                    ErrorMessages.SchemaValidatorValidateIndexesVoid2);
            }

            if (declaration.Columns.Count == 0)
            {
                Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, indexPath,
                    ErrorMessages.SchemaValidatorValidateIndexesVoid3);
            }

            ValidateMemberNames(declaration.Columns, columns, $"{indexPath}.columns", errors);
        }
    }

    private static void ValidateMemberNames(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, CompiledSchemaColumn> columns,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < names.Count; index++)
        {
            string? name = names[index];
            string memberPath = string.IsNullOrWhiteSpace(name) ? $"{path}[{index}]" : $"{path}.{name}";
            if (!RequireName(name, memberPath, errors))
            {
                continue;
            }

            if (!unique.Add(name!))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, memberPath,
                    ErrorMessages.ValidatorValidateMemberNamesVoid);
            }

            if (!columns.ContainsKey(name!))
            {
                Add(errors, SqlSchemaValidationErrorCode.UnknownReference, memberPath,
                    ErrorMessages.ValidatorValidateMemberNamesVoid2);
            }
        }
    }

    private static void ValidateTypeReference(
        DatabaseType type,
        string? customType,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        if (!Enum.IsDefined(type) || type == DatabaseType.Null)
        {
            Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, path,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidatorValidateTypeReferenceVoid, type));
        }

        if (customType is null)
        {
            return;
        }

        if (!RequireName(customType, $"{path}.customType", errors))
        {
            return;
        }

        if (!types.TryGetValue(customType, out CompiledSchemaType? declaration))
        {
            // Not a type the schema declares: a domain the SQL engine registers, or a built-in type
            // name. Shape and built-in modifiers are validated here; domain existence is the engine's.
            if (!CompiledSchemaTypeNames.IsTypeName(customType))
            {
                Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, $"{path}.customType",
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidatorValidateTypeReferenceVoid2, customType));
            }
            else if (SqlTypeModifiers.TrySplit(customType, out string name, out int? first, out int? second) &&
                SqlTypeModifiers.GetError(name, first, second) is { } error)
            {
                Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, path, error);
            }
        }
        else if (declaration.StorageType != type)
        {
            Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, $"{path}.customType",
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidatorValidateTypeReferenceVoid3, customType, declaration.StorageType, type));
        }
    }

    private static void ValidateStorageType(
        DatabaseType type,
        int? length,
        int? precision,
        int? scale,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        if (!Enum.IsDefined(type) || type == DatabaseType.Null)
        {
            Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, $"{path}.type",
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValidatorValidateTypeReferenceVoid, type));
            return;
        }

        if (SqlTypeModifiers.GetError(type, length, precision, scale) is { } error)
        {
            Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, path, error);
        }
    }

    private static void ValidateExpression(
        CompiledSchemaExpression? expression,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        if (expression is null || string.IsNullOrWhiteSpace(expression.CanonicalText))
        {
            Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, path,
                ErrorMessages.SchemaValidatorValidateExpressionVoid);
        }
    }

    private static bool TypesEqual(CompiledSchemaColumn left, CompiledSchemaColumn right)
        => left.Type == right.Type &&
            string.Equals(left.CustomType, right.CustomType, StringComparison.OrdinalIgnoreCase);

    private static bool RequireName(
        string? value,
        string declaration,
        List<SqlSchemaValidationError> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, declaration,
            ErrorMessages.SchemaValidatorRequireNameVoid);
        return false;
    }

    private static string NamedPath(string root, string? name, int index)
        => string.IsNullOrWhiteSpace(name) ? $"{root}[{index}]" : $"{root}.{name}";

    private static void Add(
        List<SqlSchemaValidationError> errors,
        SqlSchemaValidationErrorCode code,
        string declaration,
        string message)
        => errors.Add(new SqlSchemaValidationError(code, declaration, message));
}
