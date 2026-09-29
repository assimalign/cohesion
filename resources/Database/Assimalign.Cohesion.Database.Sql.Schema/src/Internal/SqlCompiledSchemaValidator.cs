using System;
using System.Collections.Generic;

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

        var errors = new List<SqlSchemaValidationError>();
        if (!string.Equals(schema.Format, SqlCompiledSchema.CurrentFormat, StringComparison.Ordinal))
        {
            Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, "schema.format",
                $"Compiled schema format '{schema.Format}' is not supported.");
        }

        RequireName(schema.Name, "schema.name", errors);
        if (schema.Model != EngineModel.Sql)
        {
            Add(errors, SqlSchemaValidationErrorCode.ModelMismatch, "schema.model",
                $"Engine model '{schema.Model}' cannot own a SQL compiled schema.");
        }

        Dictionary<string, CompiledSchemaType> types = ValidateTypes(schema.Types, errors);
        Dictionary<string, CompiledSchemaTable> tables = CollectTables(schema.Tables, errors);
        Dictionary<CompiledSchemaTable, Dictionary<string, CompiledSchemaColumn>> tableColumns =
            ValidateTables(schema.Tables, types, errors);
        ValidateTableConstraints(schema.Tables, tables, tableColumns, errors);

        Dictionary<string, CompiledSchemaFunction> functions =
            ValidateFunctions(schema.Functions, types, errors);
        ValidateTriggers(schema.Triggers, tables, errors);
        ValidatePrincipals(schema.Principals, tables, functions, errors);
        ValidateExtensions(schema.Extensions, errors);

        if (errors.Count > 0)
        {
            throw new SqlSchemaValidationException(errors);
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
                    "The custom type entry cannot be null.");
                continue;
            }

            if (!RequireName(declaration.Name, path, errors))
            {
                continue;
            }

            if (!result.TryAdd(declaration.Name, declaration))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The custom type name is declared more than once.");
            }

            ValidateStorageType(
                declaration.StorageType,
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
                    "The table entry cannot be null.");
                continue;
            }

            if (RequireName(table.Name, path, errors) && !result.TryAdd(table.Name, table))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The table name is declared more than once.");
            }

            if (!RequireName(table.RowType, $"{path}.rowType", errors))
            {
                continue;
            }

            if (!rowTypes.Add(table.RowType))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, $"{path}.rowType",
                    $"CLR row type '{table.RowType}' is mapped more than once.");
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
                    "A table must declare at least one column.");
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
                        "The constraint entry cannot be null.");
                    continue;
                }

                if (RequireName(constraint.Name, path, errors) && !names.Add(constraint.Name))
                {
                    Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                        "The constraint name is declared more than once.");
                }

                if (!Enum.IsDefined(constraint.Kind))
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, $"{path}.kind",
                        $"Constraint kind '{constraint.Kind}' is not defined.");
                    continue;
                }

                if (!Enum.IsDefined(constraint.OnDelete) ||
                    (constraint.Kind == CompiledSchemaConstraintKind.Check && constraint.OnDelete != CompiledSchemaReferentialAction.Restrict))
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, $"{path}.onDelete",
                        "The referential delete action is invalid for this constraint.");
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
                        $"Referenced table '{constraint.ReferencedObject}' is not declared.");
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
                        "A reference must pair every local column with one referenced column.");
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
                            $"Reference column type does not match '{target.Name}.{referenced.Name}'.");
                    }
                }
            }
        }
    }

    private static Dictionary<string, CompiledSchemaFunction> ValidateFunctions(
        IReadOnlyList<CompiledSchemaFunction> declarations,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        List<SqlSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaFunction>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaFunction? function = declarations[index];
            string path = NamedPath("functions", function?.Name, index);
            if (function is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, path,
                    "The function entry cannot be null.");
                continue;
            }

            if (RequireName(function.Name, path, errors) && !result.TryAdd(function.Name, function))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The function name is declared more than once.");
            }

            ValidateTypeReference(function.ResultType, function.CustomResultType, types, $"{path}.resultType", errors);
            var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
            {
                CompiledSchemaParameter? parameter = function.Parameters[parameterIndex];
                string parameterPath = NamedPath($"{path}.parameters", parameter?.Name, parameterIndex);
                if (parameter is null)
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, parameterPath,
                        "The function parameter cannot be null.");
                    continue;
                }

                if (RequireName(parameter.Name, parameterPath, errors) && !parameterNames.Add(parameter.Name))
                {
                    Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, parameterPath,
                        "The function parameter name is declared more than once.");
                }

                ValidateTypeReference(parameter.Type, parameter.CustomType, types, $"{parameterPath}.type", errors);
            }

            ValidateExpression(function.Body, $"{path}.body", errors);
        }

        return result;
    }

    private static void ValidateTriggers(
        IReadOnlyList<CompiledSchemaTrigger> declarations,
        IReadOnlyDictionary<string, CompiledSchemaTable> tables,
        List<SqlSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaTrigger? trigger = declarations[index];
            string path = NamedPath("triggers", trigger?.Name, index);
            if (trigger is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, path,
                    "The trigger entry cannot be null.");
                continue;
            }

            if (RequireName(trigger.Name, path, errors) && !names.Add(trigger.Name))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The trigger name is declared more than once.");
            }

            if (!RequireName(trigger.Table, $"{path}.table", errors) || !tables.ContainsKey(trigger.Table))
            {
                Add(errors, SqlSchemaValidationErrorCode.UnknownReference, $"{path}.table",
                    $"Trigger table '{trigger.Table}' is not declared.");
            }

            if (!Enum.IsDefined(trigger.Event))
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, $"{path}.event",
                    $"Trigger event '{trigger.Event}' is not defined.");
            }

            ValidateExpression(trigger.Body, $"{path}.body", errors);
        }
    }

    private static void ValidatePrincipals(
        IReadOnlyList<CompiledSchemaPrincipal> declarations,
        IReadOnlyDictionary<string, CompiledSchemaTable> tables,
        IReadOnlyDictionary<string, CompiledSchemaFunction> functions,
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
                    "The principal entry cannot be null.");
                continue;
            }

            if (RequireName(principal.Name, path, errors) && !names.Add(principal.Name))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The principal name is declared more than once.");
            }

            var grantedPermissions = new HashSet<SqlPermission>();
            for (int grantIndex = 0; grantIndex < principal.Grants.Count; grantIndex++)
            {
                CompiledSchemaGrant? grant = principal.Grants[grantIndex];
                string grantPath = $"{path}.grants[{grantIndex}]";
                if (grant is null)
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, grantPath,
                        "The grant entry cannot be null.");
                    continue;
                }

                if (!Enum.IsDefined(grant.Permission))
                {
                    Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, $"{grantPath}.permission",
                        $"SqlPermission '{grant.Permission}' is not defined.");
                }
                else if (!grantedPermissions.Add(grant.Permission))
                {
                    Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, grantPath,
                        $"SqlPermission '{grant.Permission}' is granted more than once.");
                }

                if (grant.Objects.Count == 0)
                {
                    Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, grantPath,
                        "A grant must name at least one schema object.");
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
                            $"{objectPath}.{objectName}", "The grant names the same object more than once.");
                    }

                    if (!tables.ContainsKey(objectName!) &&
                        !functions.ContainsKey(objectName!))
                    {
                        Add(errors, SqlSchemaValidationErrorCode.UnknownReference,
                            $"{objectPath}.{objectName}", "The granted schema object is not declared.");
                    }
                }

            }
        }
    }

    private static void ValidateExtensions(
        IReadOnlyList<CompiledSchemaExtension> declarations,
        List<SqlSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaExtension? extension = declarations[index];
            string path = NamedPath("extensions", extension?.Name, index);
            if (extension is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.InvalidDocument, path,
                    "The extension entry cannot be null.");
                continue;
            }

            if (RequireName(extension.Name, path, errors) && !names.Add(extension.Name))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The extension name is declared more than once.");
            }

            if (extension.Value is null)
            {
                Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, $"{path}.value",
                    "The extension value cannot be null.");
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
                    "The column or field entry cannot be null.");
                continue;
            }

            if (RequireName(column.Name, columnPath, errors) && !result.TryAdd(column.Name, column))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, columnPath,
                    "The column or field name is declared more than once.");
            }

            ValidateTypeReference(column.Type, column.CustomType, types, $"{columnPath}.type", errors);
            ValidateStorageType(column.Type, column.Precision, column.Scale, columnPath, errors);
            if (column.MaxLength is <= 0)
            {
                Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, $"{columnPath}.maxLength",
                    "Maximum length must be greater than zero.");
            }
            else if (column.MaxLength is not null && column.Type is not DatabaseType.String and not DatabaseType.Binary)
            {
                Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, $"{columnPath}.maxLength",
                    "Maximum length is supported only for string and binary values.");
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
                "A key must declare at least one column or field.");
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
                    "The index entry cannot be null.");
                continue;
            }

            if (RequireName(declaration.Name, indexPath, errors) && !names.Add(declaration.Name))
            {
                Add(errors, SqlSchemaValidationErrorCode.DuplicateDeclaration, indexPath,
                    "The index name is declared more than once.");
            }

            if (declaration.Columns.Count == 0)
            {
                Add(errors, SqlSchemaValidationErrorCode.IncompleteDeclaration, indexPath,
                    "An index must declare at least one column or field.");
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
                    "The same column or field is named more than once.");
            }

            if (!columns.ContainsKey(name!))
            {
                Add(errors, SqlSchemaValidationErrorCode.UnknownReference, memberPath,
                    "The column or field is not declared.");
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
                $"Database type '{type}' is not a supported stored value type.");
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
            Add(errors, SqlSchemaValidationErrorCode.UnknownReference, $"{path}.customType",
                $"Custom type '{customType}' is not declared.");
        }
        else if (declaration.StorageType != type)
        {
            Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, $"{path}.customType",
                $"Custom type '{customType}' uses {declaration.StorageType}, not {type}.");
        }
    }

    private static void ValidateStorageType(
        DatabaseType type,
        int? precision,
        int? scale,
        string path,
        List<SqlSchemaValidationError> errors)
    {
        if (!Enum.IsDefined(type) || type == DatabaseType.Null)
        {
            Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, $"{path}.type",
                $"Database type '{type}' is not a supported stored value type.");
            return;
        }

        if (precision is null && scale is null)
        {
            return;
        }

        if (type != DatabaseType.Decimal || precision is null || scale is null ||
            precision <= 0 || scale < 0 || scale > precision)
        {
            Add(errors, SqlSchemaValidationErrorCode.UnsupportedType, $"{path}.precision",
                "Precision and scale must form a valid decimal storage definition.");
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
                "A canonical expression is required.");
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
            "A non-empty declaration name is required.");
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
