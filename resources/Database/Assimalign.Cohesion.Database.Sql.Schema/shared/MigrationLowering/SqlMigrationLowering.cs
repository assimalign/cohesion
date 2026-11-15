using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

using ErrorMessages = Assimalign.Cohesion.Database.Sql.Schema.Internal.SqlMigrationLoweringErrorMessages;

// Deviates from the repo RootNamespace rule per shared-source design: stateless migration
// lowering keeps its owning schema namespace in the engine and SDK task assemblies.
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>Per-statement ALTER COLUMN lowering, shared without an engine dependency.</summary>
internal static class SqlMigrationLowering
{
    // The caller owns literal parsing and its diagnostic carrier. No callbacks or state are retained.
    internal static void RenderAlterColumns(SqlSchemaMigrationOperation operation,
        Action<SqlSchemaMigrationOperation, string, string?> emit, Func<string?, string?> canonicalDefault)
    {
        try { RenderAlterColumnsCore(operation, emit, canonicalDefault); }
        catch (SqlSchemaMigrationException) { throw; }
        catch (Exception exception) when (exception is DatabaseException or ArgumentException)
        {
            throw new SqlSchemaMigrationException(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.MigrationScriptGeneratorRenderFailure, operation.Kind, QualifiedName(operation), exception.Message), exception);
        }
    }

    private static void RenderAlterColumnsCore(SqlSchemaMigrationOperation operation, Action<SqlSchemaMigrationOperation, string, string?> emit, Func<string?, string?> canonicalDefault)
    {
        if (operation.Column is not { } column || operation.PreviousColumn is not { } previous)
        {
            throw InvalidPayload(operation, "the previous and desired column definitions");
        }
        RequireMatchingName(operation, column.Name, "column");
        DatabaseTypeInfo type = ResolveAlterColumnType(column, out string? domain);
        DatabaseTypeInfo previousType = ResolveAlterColumnType(previous, out string? previousDomain);
        bool changesDefault = !string.Equals(canonicalDefault(column.DefaultLiteral),
            canonicalDefault(previous.DefaultLiteral), StringComparison.Ordinal);
        string table = RequireParent(operation);
        bool changesType = type.Type != previousType.Type || type.MaxLength != previousType.MaxLength ||
            type.Precision != previousType.Precision || type.Scale != previousType.Scale ||
            !string.Equals(domain, previousDomain, StringComparison.OrdinalIgnoreCase);
        bool rewrite = RequiresTypeRewrite(previousType, previousDomain, type, domain);
        if (!rewrite && !changesDefault)
        {
            RenderAlterColumn(operation, emit, canonicalDefault);
            return;
        }

        // A portable AlterColumn already carries both typed definitions. Lower its TYPE
        // before any nullability change; both retain their place in declaration order.
        // Reverse conversion can lose data even when an assignment cast exists, so
        // TYPE has no compensating SQL. Joint execution consumes the complete plan.
        var typed = column with { IsNullable = previous.IsNullable, DefaultLiteral = null };
        if (rewrite)
        {
            // The complete declaration supplies the final default. Remove the old
            // override before TYPE so an obsolete expression cannot block conversion.
            if (previous.DefaultLiteral is not null)
            {
                var drop = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                    operation.ObjectName, operation.ParentName, column: previous with { DefaultLiteral = null }, previousColumn: previous);
                emit(drop,
                    SetDefault(table, previous with { DefaultLiteral = null }, canonicalDefault),
                    SetDefault(table, previous, canonicalDefault));
            }
            var typeOperation = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: typed, previousColumn: previous);
            emit(typeOperation,
                AlterColumnType(table, column), null);
        }
        else if (changesType)
        {
            // Preserve reversible full-definition lowering for compatible
            // facets, including when a separate default change is also requested.
            typed = column with { IsNullable = previous.IsNullable, DefaultLiteral = previous.DefaultLiteral };
            RenderAlterColumn(new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: typed, previousColumn: previous), emit, canonicalDefault);
        }
        if (changesDefault || rewrite && column.DefaultLiteral is not null)
        {
            // The declaration's explicit default is authoritative afterwards (for example 1.234 declared
            // on NUMERIC(8,2) is stored as that expression, while TYPE folds it to 1.23).
            // A separate existing DDL action keeps the exported statement sequence
            // faithful to that declaration. Joint publication includes the final default.
            var defaultOperation = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: column with { IsNullable = previous.IsNullable }, previousColumn: typed);
            emit(defaultOperation, SetDefault(table, column, canonicalDefault),
                rewrite ? null : SetDefault(table, previous, canonicalDefault));
        }
        if (previous.IsNullable != column.IsNullable)
        {
            var nullableOperation = new SqlSchemaMigrationOperation(operation.Kind, operation.Safety,
                operation.ObjectName, operation.ParentName, column: column, previousColumn: column with { IsNullable = previous.IsNullable });
            emit(nullableOperation,
                SetNullability(table, column),
                SetNullability(table, typed));
        }
    }

    // Same-storage precision and widening length bounds need validation or metadata
    // publication only. Changes to physical values still require a new owner.
    internal static bool RequiresTypeRewrite(DatabaseTypeInfo previous, string? previousDomain, DatabaseTypeInfo type, string? domain)
        => type.Type != previous.Type || type.Scale != previous.Scale ||
            !string.Equals(domain, previousDomain, StringComparison.OrdinalIgnoreCase) ||
            type.Type == DatabaseType.Character && type.MaxLength != previous.MaxLength ||
            type.Type == DatabaseType.String && type.MaxLength is { } length &&
                (previous.MaxLength is null || length < previous.MaxLength);

    private static void RenderAlterColumn(SqlSchemaMigrationOperation operation,
        Action<SqlSchemaMigrationOperation, string, string?> emit, Func<string?, string?> canonicalDefault)
        => emit(operation, AlterColumn(RequireParent(operation), operation.Column!, canonicalDefault),
            AlterColumn(RequireParent(operation), operation.PreviousColumn!, canonicalDefault));

    internal static DatabaseTypeInfo ResolveAlterColumnType(CompiledSchemaColumn column, out string? domain)
    {
        domain = null;
        if (column.CustomType is not { } declared)
        {
            return new DatabaseTypeInfo(column.Type, column.MaxLength, column.Precision,
                SqlTypeModifiers.NormalizeScale(column.Type, column.Precision, column.Scale));
        }
        if (SqlTypeModifiers.TrySplit(declared, out string builtInName, out int? builtInFirst, out int? builtInSecond) &&
            SqlTypeModifiers.GetError(builtInName, builtInFirst, builtInSecond, isDelimited: declared.TrimStart().StartsWith('"')) is null &&
            SqlTypeNames.TryResolve(builtInName, builtInFirst, null, builtInSecond, out var storage))
        {
            if (storage.Type != column.Type && !(storage.Type == DatabaseType.Character && column.Type == DatabaseType.String))
            {
                throw new DatabaseException(string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.ProvisionerResolveColumnTypeDatabase2, column.Name, declared,
                    DiagnosticTypeName(storage.Type), DiagnosticTypeName(column.Type)));
            }
            return storage;
        }

        // Script generation has no engine catalog. An engine-owned domain is compared by
        // identity; its immutable bounds are validated when provisioning binds the schema.
        if (!SqlTypeModifiers.TrySplit(declared, out string name, out int? first, out _) || first is not null)
        {
            throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ResolveAlterColumnTypeDatabase, column.Name, declared));
        }
        ValidateDomainName(name);
        domain = name;
        return new DatabaseTypeInfo(column.Type, column.MaxLength, column.Precision, column.Scale);
    }

    internal static string AlterColumn(string tableName, CompiledSchemaColumn column, Func<string?, string?> canonicalDefault)
    {
        var builder = new StringBuilder($"ALTER TABLE {QualifiedTable(tableName)} ALTER COLUMN ");
        builder.Append(Identifier(column.Name)).Append(' ').Append(TypeName(column))
            .Append(column.IsNullable ? " NULL" : " NOT NULL");
        if (column.DefaultLiteral is not null)
        {
            builder.Append(" DEFAULT ").Append(canonicalDefault(column.DefaultLiteral) ?? "NULL");
        }
        return builder.Append(';').ToString();
    }

    internal static string AlterColumnType(string tableName, CompiledSchemaColumn column)
        => $"ALTER TABLE {QualifiedTable(tableName)} ALTER COLUMN {Identifier(column.Name)} TYPE {TypeName(column)};";

    internal static string SetNullability(string tableName, CompiledSchemaColumn column)
        => $"ALTER TABLE {QualifiedTable(tableName)} ALTER COLUMN {Identifier(column.Name)} {(column.IsNullable ? "DROP" : "SET")} NOT NULL;";

    internal static string SetDefault(string tableName, CompiledSchemaColumn column, Func<string?, string?> canonicalDefault)
        => $"ALTER TABLE {QualifiedTable(tableName)} ALTER COLUMN {Identifier(column.Name)} " +
            (column.DefaultLiteral is null ? "DROP DEFAULT;" : $"SET DEFAULT {canonicalDefault(column.DefaultLiteral) ?? "NULL"};");

    internal static string TypeName(CompiledSchemaColumn column)
    {
        if (column.CustomType is { } declared)
        {
            // A domain's name or a built-in type name as declared, which the engine's DDL
            // resolves as it resolves any type name. Only the name's shape is checked here: rewritten
            // from its parts, so the text written into the statement is nothing but a type name.
            if (!SqlTypeModifiers.TrySplit(declared, out string name, out int? first, out int? second) ||
                !SqlTypeModifiers.IsTypeName(name) || (second is not null && first is null))
            {
                throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StatementRendererTypeNameDatabase, column.Name, declared));
            }

            return first is null
                ? name
                : second is null
                    ? $"{name}({first.Value.ToString(CultureInfo.InvariantCulture)})"
                    : $"{name}({first.Value.ToString(CultureInfo.InvariantCulture)}, {second.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        return column.Type switch
        {
            DatabaseType.Boolean => "BOOLEAN",
            DatabaseType.Int8 => "TINYINT",
            DatabaseType.Int16 => "SMALLINT",
            DatabaseType.Int32 => "INT",
            DatabaseType.Int64 => "BIGINT",
            DatabaseType.Float32 => "REAL",
            DatabaseType.Float64 => "DOUBLE",
            DatabaseType.Decimal => DecimalTypeName(column),
            DatabaseType.Character => SizedTypeName("CHAR", "CHAR", column.MaxLength ?? 1),
            DatabaseType.String => SizedTypeName("VARCHAR", "TEXT", column.MaxLength),
            DatabaseType.Binary => SizedTypeName("VARBINARY", "BLOB", column.MaxLength),
            DatabaseType.Date => "DATE",
            DatabaseType.Time => "TIME",
            DatabaseType.DateTime => "TIMESTAMP",
            DatabaseType.DateTimeOffset => "TIMESTAMPTZ",
            DatabaseType.TimeSpan => "INTERVAL",
            DatabaseType.Guid => "UUID",
            DatabaseType.Json => "JSON",
            DatabaseType.JsonBinary => "JSONB",
            _ => throw new DatabaseException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StatementRendererTypeNameDatabase2, column.Name, column.Type)),
        };
    }

    private static string DecimalTypeName(CompiledSchemaColumn column)
    {
        if (column.Scale is not null && column.Precision is null)
        {
            throw new DatabaseException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RendererDecimalTypeNameDatabase, column.Name));
        }

        if (column.Precision is null)
        {
            return "DECIMAL";
        }

        string precision = column.Precision.Value.ToString(CultureInfo.InvariantCulture);
        return column.Scale is null
            ? $"DECIMAL({precision})"
            : $"DECIMAL({precision},{column.Scale.Value.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string SizedTypeName(string sizedName, string unboundedName, int? maxLength)
        => maxLength is null
            ? unboundedName
            : $"{sizedName}({maxLength.Value.ToString(CultureInfo.InvariantCulture)})";

    private static string QualifiedTable(string tableName)
        => $"{SqlNamespaceNames.Default}.{Identifier(tableName)}";

    internal static string Identifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.IndexOfAny(['\0', '"']) >= 0)
        {
            throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StatementRendererIdentifierDatabase, value));
        }

        bool requiresQuotes = !(char.IsLetter(value[0]) || value[0] == '_');
        for (int index = 1; index < value.Length; index++)
        {
            if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_'))
            {
                requiresQuotes = true;
            }
        }

        foreach (string keyword in SqlLanguageProfile.Instance.Keywords)
        {
            requiresQuotes |= string.Equals(value, keyword, StringComparison.OrdinalIgnoreCase);
        }
        foreach (string word in SqlLanguageProfile.RecognizedUnsupportedVocabulary)
        {
            requiresQuotes |= string.Equals(value, word, StringComparison.OrdinalIgnoreCase);
        }
        foreach (string function in SqlLanguageProfile.Instance.Functions)
        {
            requiresQuotes |= string.Equals(value, function, StringComparison.OrdinalIgnoreCase);
        }
        return requiresQuotes ? "\"" + value + "\"" : value;
    }
    private static void ValidateDomainName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TypeRulesValidateDomainNameArgument, name), nameof(name));
        }
        foreach (char character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                throw new ArgumentException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TypeRulesValidateDomainNameArgument2, name), nameof(name));
            }
        }
        ValidateSqlDomainName(name);
    }

    // SQL has already lexed a delimited name, so punctuation is part of its value.
    // Native registrations keep the ASCII name contract the typed schema consumes.
    private static void ValidateSqlDomainName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.ToUpperInvariant() is "SERIAL" or "BIGSERIAL" or "SMALLSERIAL")
        {
            throw new ArgumentException(SqlTypeModifiers.GetError(name, null, null), nameof(name));
        }
        foreach (string keyword in SqlLanguageProfile.Instance.Keywords)
        {
            if (string.Equals(keyword, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RulesValidateSqlDomainNameArgument, name), nameof(name));
            }
        }
        foreach (string reserved in new[] { "SOME", "LOCALTIME", "LOCALTIMESTAMP" })
        {
            if (string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RulesValidateSqlDomainNameArgument2, name), nameof(name));
            }
        }
        foreach (string pseudo in new[] { "ANY", "ANYELEMENT", "ANYNUMERIC" })
        {
            if (string.Equals(pseudo, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RulesValidateSqlDomainNameArgument3, name), nameof(name));
            }
        }
        if (SqlTypeNames.TryResolve(name, out _) || SqlTypeModifiers.Rejection(name) is not null)
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RulesValidateSqlDomainNameArgument4, name), nameof(name));
        }
    }

    internal static string DiagnosticTypeName(DatabaseType type)
        => type switch
        {
            DatabaseType.Int32 => "INTEGER",
            DatabaseType.Decimal => "NUMERIC",
            DatabaseType.Character => "CHAR(1)",
            DatabaseType.Binary => "BINARY",
            DatabaseType.Null => "unknown",
            _ when !Enum.IsDefined(type) => "unknown",
            _ => TypeName(new CompiledSchemaColumn("type", type, true)),
        };

    private static void RequireMatchingName(
        SqlSchemaMigrationOperation operation,
        string payloadName,
        string payloadKind)
    {
        if (!string.Equals(operation.ObjectName, payloadName, StringComparison.Ordinal))
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GeneratorRequireMatchingNameFailure, operation.Kind, operation.ObjectName, payloadKind, payloadName));
        }
    }

    private static string RequireParent(SqlSchemaMigrationOperation operation)
        => !string.IsNullOrWhiteSpace(operation.ParentName)
            ? operation.ParentName
            : throw InvalidPayload(operation, "an owning table name");

    private static SqlSchemaMigrationException InvalidPayload(
        SqlSchemaMigrationOperation operation,
        string expected)
        => new(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ScriptGeneratorInvalidPayloadFailure, operation.Kind, QualifiedName(operation), expected));

    private static string QualifiedName(SqlSchemaMigrationOperation operation)
        => operation.ParentName is null
            ? operation.ObjectName
            : $"{operation.ParentName}.{operation.ObjectName}";
}
