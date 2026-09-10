using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Validates the portable compiled-schema contract at every trust boundary.
/// </summary>
internal static class CompiledSchemaValidator
{
    internal static void Validate(CompiledSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var errors = new List<DatabaseSchemaValidationError>();
        if (!string.Equals(schema.Format, CompiledSchema.CurrentFormat, StringComparison.Ordinal))
        {
            Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, "schema.format",
                $"Compiled schema format '{schema.Format}' is not supported.");
        }

        RequireName(schema.Name, "schema.name", errors);
        if (!Enum.IsDefined(schema.Model) || schema.Model == EngineModel.Custom)
        {
            Add(errors, DatabaseSchemaValidationErrorCode.ModelMismatch, "schema.model",
                $"Engine model '{schema.Model}' cannot own a compiled schema.");
        }

        if (schema.Model == EngineModel.KeyValueStore && schema.Tables.Count > 0)
        {
            Add(errors, DatabaseSchemaValidationErrorCode.ModelMismatch, "schema.tables",
                "The key-value engine model accepts collections, not relational tables.");
        }
        else if (schema.Model != EngineModel.KeyValueStore && schema.Collections.Count > 0)
        {
            Add(errors, DatabaseSchemaValidationErrorCode.ModelMismatch, "schema.collections",
                "Collections require the key-value engine model.");
        }

        Dictionary<string, CompiledSchemaType> types = ValidateTypes(schema.Types, errors);
        Dictionary<string, CompiledSchemaTable> tables = CollectTables(schema.Tables, errors);
        Dictionary<CompiledSchemaTable, Dictionary<string, CompiledSchemaColumn>> tableColumns =
            ValidateTables(schema.Tables, types, errors);
        ValidateTableConstraints(schema.Tables, tables, tableColumns, errors);

        Dictionary<string, CompiledSchemaCollection> collections =
            ValidateCollections(schema.Collections, types, errors);
        Dictionary<string, CompiledSchemaFunction> functions =
            ValidateFunctions(schema.Functions, types, errors);
        ValidateTriggers(schema.Triggers, tables, errors);
        ValidatePrincipals(schema.Principals, tables, collections, functions, errors);
        ValidateExtensions(schema.Extensions, errors);

        if (errors.Count > 0)
        {
            throw new DatabaseSchemaValidationException(errors);
        }
    }

    internal static DatabaseSchemaValidationException InvalidDocument(
        string declaration,
        string message,
        Exception? innerException = null)
        => new([
            new DatabaseSchemaValidationError(
                DatabaseSchemaValidationErrorCode.InvalidDocument,
                declaration,
                message)], innerException);

    private static Dictionary<string, CompiledSchemaType> ValidateTypes(
        IReadOnlyList<CompiledSchemaType> declarations,
        List<DatabaseSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaType>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaType? declaration = declarations[index];
            string path = NamedPath("types", declaration?.Name, index);
            if (declaration is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                    "The custom type entry cannot be null.");
                continue;
            }

            if (!RequireName(declaration.Name, path, errors))
            {
                continue;
            }

            if (!result.TryAdd(declaration.Name, declaration))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
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
        List<DatabaseSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaTable>(StringComparer.OrdinalIgnoreCase);
        var rowTypes = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaTable? table = declarations[index];
            string path = NamedPath("tables", table?.Name, index);
            if (table is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                    "The table entry cannot be null.");
                continue;
            }

            if (RequireName(table.Name, path, errors) && !result.TryAdd(table.Name, table))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The table name is declared more than once.");
            }

            if (!RequireName(table.RowType, $"{path}.rowType", errors))
            {
                continue;
            }

            if (!rowTypes.Add(table.RowType))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, $"{path}.rowType",
                    $"CLR row type '{table.RowType}' is mapped more than once.");
            }
        }

        return result;
    }

    private static Dictionary<CompiledSchemaTable, Dictionary<string, CompiledSchemaColumn>> ValidateTables(
        IReadOnlyList<CompiledSchemaTable> declarations,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        List<DatabaseSchemaValidationError> errors)
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
                Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, path,
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
        List<DatabaseSchemaValidationError> errors)
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
                    Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                        "The constraint entry cannot be null.");
                    continue;
                }

                if (RequireName(constraint.Name, path, errors) && !names.Add(constraint.Name))
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
                        "The constraint name is declared more than once.");
                }

                if (!Enum.IsDefined(constraint.Kind))
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, $"{path}.kind",
                        $"Constraint kind '{constraint.Kind}' is not defined.");
                    continue;
                }

                ValidateMemberNames(constraint.Columns, columns, $"{path}.columns", errors);
                if (constraint.Kind == CompiledSchemaConstraintKind.Check)
                {
                    ValidateExpression(constraint.Expression, $"{path}.expression", errors);
                    continue;
                }

                if (!RequireName(constraint.ReferencedObject, $"{path}.referencedObject", errors) ||
                    !tables.TryGetValue(constraint.ReferencedObject!, out CompiledSchemaTable? target) ||
                    !tableColumns.TryGetValue(target, out Dictionary<string, CompiledSchemaColumn>? targetColumns))
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.UnknownReference, $"{path}.referencedObject",
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
                    Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, path,
                        "A reference must pair every local column with one referenced column.");
                    continue;
                }

                for (int columnIndex = 0; columnIndex < constraint.Columns.Count; columnIndex++)
                {
                    if (columns.TryGetValue(constraint.Columns[columnIndex], out CompiledSchemaColumn? source) &&
                        targetColumns.TryGetValue(constraint.ReferencedColumns[columnIndex], out CompiledSchemaColumn? referenced) &&
                        !TypesEqual(source, referenced))
                    {
                        Add(errors, DatabaseSchemaValidationErrorCode.UnsupportedType,
                            $"{path}.columns.{constraint.Columns[columnIndex]}",
                            $"Reference column type does not match '{target.Name}.{referenced.Name}'.");
                    }
                }
            }
        }
    }

    private static Dictionary<string, CompiledSchemaCollection> ValidateCollections(
        IReadOnlyList<CompiledSchemaCollection> declarations,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        List<DatabaseSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaCollection>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaCollection? collection = declarations[index];
            string path = NamedPath("collections", collection?.Name, index);
            if (collection is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                    "The collection entry cannot be null.");
                continue;
            }

            if (RequireName(collection.Name, path, errors) && !result.TryAdd(collection.Name, collection))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The collection name is declared more than once.");
            }

            RequireName(collection.EntryType, $"{path}.entryType", errors);
            Dictionary<string, CompiledSchemaColumn> fields =
                ValidateColumns(collection.Fields, types, $"{path}.fields", errors);
            if (collection.Fields.Count == 0)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, path,
                    "A collection must declare at least one field.");
            }

            if (collection.Key is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, $"{path}.key",
                    "A collection must declare a key.");
            }
            else
            {
                ValidateKey(collection.Key, fields, $"{path}.key", errors);
            }

            ValidateIndexes(collection.Indexes, fields, $"{path}.indexes", errors);
        }

        return result;
    }

    private static Dictionary<string, CompiledSchemaFunction> ValidateFunctions(
        IReadOnlyList<CompiledSchemaFunction> declarations,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        List<DatabaseSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaFunction>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaFunction? function = declarations[index];
            string path = NamedPath("functions", function?.Name, index);
            if (function is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                    "The function entry cannot be null.");
                continue;
            }

            if (RequireName(function.Name, path, errors) && !result.TryAdd(function.Name, function))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
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
                    Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, parameterPath,
                        "The function parameter cannot be null.");
                    continue;
                }

                if (RequireName(parameter.Name, parameterPath, errors) && !parameterNames.Add(parameter.Name))
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, parameterPath,
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
        List<DatabaseSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaTrigger? trigger = declarations[index];
            string path = NamedPath("triggers", trigger?.Name, index);
            if (trigger is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                    "The trigger entry cannot be null.");
                continue;
            }

            if (RequireName(trigger.Name, path, errors) && !names.Add(trigger.Name))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The trigger name is declared more than once.");
            }

            if (!RequireName(trigger.Table, $"{path}.table", errors) || !tables.ContainsKey(trigger.Table))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.UnknownReference, $"{path}.table",
                    $"Trigger table '{trigger.Table}' is not declared.");
            }

            if (!Enum.IsDefined(trigger.Event))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, $"{path}.event",
                    $"Trigger event '{trigger.Event}' is not defined.");
            }

            ValidateExpression(trigger.Body, $"{path}.body", errors);
        }
    }

    private static void ValidatePrincipals(
        IReadOnlyList<CompiledSchemaPrincipal> declarations,
        IReadOnlyDictionary<string, CompiledSchemaTable> tables,
        IReadOnlyDictionary<string, CompiledSchemaCollection> collections,
        IReadOnlyDictionary<string, CompiledSchemaFunction> functions,
        List<DatabaseSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaPrincipal? principal = declarations[index];
            string path = NamedPath("principals", principal?.Name, index);
            if (principal is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                    "The principal entry cannot be null.");
                continue;
            }

            if (RequireName(principal.Name, path, errors) && !names.Add(principal.Name))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The principal name is declared more than once.");
            }

            var grantedPermissions = new HashSet<Permission>();
            for (int grantIndex = 0; grantIndex < principal.Grants.Count; grantIndex++)
            {
                CompiledSchemaGrant? grant = principal.Grants[grantIndex];
                string grantPath = $"{path}.grants[{grantIndex}]";
                if (grant is null)
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, grantPath,
                        "The grant entry cannot be null.");
                    continue;
                }

                if (!Enum.IsDefined(grant.Permission))
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, $"{grantPath}.permission",
                        $"Permission '{grant.Permission}' is not defined.");
                }
                else if (!grantedPermissions.Add(grant.Permission))
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, grantPath,
                        $"Permission '{grant.Permission}' is granted more than once.");
                }

                if (grant.Objects.Count == 0)
                {
                    Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, grantPath,
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
                        Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration,
                            $"{objectPath}.{objectName}", "The grant names the same object more than once.");
                    }

                    if (!tables.ContainsKey(objectName!) &&
                        !collections.ContainsKey(objectName!) &&
                        !functions.ContainsKey(objectName!))
                    {
                        Add(errors, DatabaseSchemaValidationErrorCode.UnknownReference,
                            $"{objectPath}.{objectName}", "The granted schema object is not declared.");
                    }
                }

            }
        }
    }

    private static void ValidateExtensions(
        IReadOnlyList<CompiledSchemaExtension> declarations,
        List<DatabaseSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaExtension? extension = declarations[index];
            string path = NamedPath("extensions", extension?.Name, index);
            if (extension is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, path,
                    "The extension entry cannot be null.");
                continue;
            }

            if (RequireName(extension.Name, path, errors) && !names.Add(extension.Name))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, path,
                    "The extension name is declared more than once.");
            }

            if (extension.Value is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, $"{path}.value",
                    "The extension value cannot be null.");
            }
        }
    }

    private static Dictionary<string, CompiledSchemaColumn> ValidateColumns(
        IReadOnlyList<CompiledSchemaColumn> declarations,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        string path,
        List<DatabaseSchemaValidationError> errors)
    {
        var result = new Dictionary<string, CompiledSchemaColumn>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < declarations.Count; index++)
        {
            CompiledSchemaColumn? column = declarations[index];
            string columnPath = NamedPath(path, column?.Name, index);
            if (column is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, columnPath,
                    "The column or field entry cannot be null.");
                continue;
            }

            if (RequireName(column.Name, columnPath, errors) && !result.TryAdd(column.Name, column))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, columnPath,
                    "The column or field name is declared more than once.");
            }

            ValidateTypeReference(column.Type, column.CustomType, types, $"{columnPath}.type", errors);
            ValidateStorageType(column.Type, column.Precision, column.Scale, columnPath, errors);
            if (column.MaxLength is <= 0)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, $"{columnPath}.maxLength",
                    "Maximum length must be greater than zero.");
            }
            else if (column.MaxLength is not null && column.Type is not DatabaseType.String and not DatabaseType.Binary)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.UnsupportedType, $"{columnPath}.maxLength",
                    "Maximum length is supported only for string and binary values.");
            }
        }

        return result;
    }

    private static void ValidateKey(
        CompiledSchemaKey key,
        IReadOnlyDictionary<string, CompiledSchemaColumn> columns,
        string path,
        List<DatabaseSchemaValidationError> errors)
    {
        RequireName(key.Name, $"{path}.name", errors);
        if (key.Columns.Count == 0)
        {
            Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, path,
                "A key must declare at least one column or field.");
        }

        ValidateMemberNames(key.Columns, columns, $"{path}.columns", errors);
    }

    private static void ValidateIndexes(
        IReadOnlyList<CompiledSchemaIndex> indexes,
        IReadOnlyDictionary<string, CompiledSchemaColumn> columns,
        string path,
        List<DatabaseSchemaValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < indexes.Count; index++)
        {
            CompiledSchemaIndex? declaration = indexes[index];
            string indexPath = NamedPath(path, declaration?.Name, index);
            if (declaration is null)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.InvalidDocument, indexPath,
                    "The index entry cannot be null.");
                continue;
            }

            if (RequireName(declaration.Name, indexPath, errors) && !names.Add(declaration.Name))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, indexPath,
                    "The index name is declared more than once.");
            }

            if (declaration.Columns.Count == 0)
            {
                Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, indexPath,
                    "An index must declare at least one column or field.");
            }

            ValidateMemberNames(declaration.Columns, columns, $"{indexPath}.columns", errors);
        }
    }

    private static void ValidateMemberNames(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, CompiledSchemaColumn> columns,
        string path,
        List<DatabaseSchemaValidationError> errors)
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
                Add(errors, DatabaseSchemaValidationErrorCode.DuplicateDeclaration, memberPath,
                    "The same column or field is named more than once.");
            }

            if (!columns.ContainsKey(name!))
            {
                Add(errors, DatabaseSchemaValidationErrorCode.UnknownReference, memberPath,
                    "The column or field is not declared.");
            }
        }
    }

    private static void ValidateTypeReference(
        DatabaseType type,
        string? customType,
        IReadOnlyDictionary<string, CompiledSchemaType> types,
        string path,
        List<DatabaseSchemaValidationError> errors)
    {
        if (!Enum.IsDefined(type) || type == DatabaseType.Null)
        {
            Add(errors, DatabaseSchemaValidationErrorCode.UnsupportedType, path,
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
            Add(errors, DatabaseSchemaValidationErrorCode.UnknownReference, $"{path}.customType",
                $"Custom type '{customType}' is not declared.");
        }
        else if (declaration.StorageType != type)
        {
            Add(errors, DatabaseSchemaValidationErrorCode.UnsupportedType, $"{path}.customType",
                $"Custom type '{customType}' uses {declaration.StorageType}, not {type}.");
        }
    }

    private static void ValidateStorageType(
        DatabaseType type,
        int? precision,
        int? scale,
        string path,
        List<DatabaseSchemaValidationError> errors)
    {
        if (!Enum.IsDefined(type) || type == DatabaseType.Null)
        {
            Add(errors, DatabaseSchemaValidationErrorCode.UnsupportedType, $"{path}.type",
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
            Add(errors, DatabaseSchemaValidationErrorCode.UnsupportedType, $"{path}.precision",
                "Precision and scale must form a valid decimal storage definition.");
        }
    }

    private static void ValidateExpression(
        CompiledSchemaExpression? expression,
        string path,
        List<DatabaseSchemaValidationError> errors)
    {
        if (expression is null || string.IsNullOrWhiteSpace(expression.CanonicalText))
        {
            Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, path,
                "A canonical expression is required.");
        }
    }

    private static bool TypesEqual(CompiledSchemaColumn left, CompiledSchemaColumn right)
        => left.Type == right.Type &&
            string.Equals(left.CustomType, right.CustomType, StringComparison.OrdinalIgnoreCase);

    private static bool RequireName(
        string? value,
        string declaration,
        List<DatabaseSchemaValidationError> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        Add(errors, DatabaseSchemaValidationErrorCode.IncompleteDeclaration, declaration,
            "A non-empty declaration name is required.");
        return false;
    }

    private static string NamedPath(string root, string? name, int index)
        => string.IsNullOrWhiteSpace(name) ? $"{root}[{index}]" : $"{root}.{name}";

    private static void Add(
        List<DatabaseSchemaValidationError> errors,
        DatabaseSchemaValidationErrorCode code,
        string declaration,
        string message)
        => errors.Add(new DatabaseSchemaValidationError(code, declaration, message));
}
