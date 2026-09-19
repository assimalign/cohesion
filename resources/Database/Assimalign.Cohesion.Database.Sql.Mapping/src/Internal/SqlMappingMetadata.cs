using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

internal sealed class SqlMappingMetadata
{
    internal SqlMappingMetadata(string table, IReadOnlyList<string> columns, IReadOnlyList<DatabaseType> types,
        string key, IReadOnlyList<string> references)
    {
        _ = SqlMappingText.Identifier(table);
        _ = SqlMappingText.Identifier(key);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(references);
        if (columns.Count == 0)
        {
            throw new ArgumentException("A SQL entity mapping must declare at least one column.", nameof(columns));
        }
        Table = table;
        Key = key;
        Columns = new string[columns.Count];
        Types = new DatabaseType[columns.Count];
        if (types.Count != columns.Count)
        {
            throw new ArgumentException("Every mapped column must have one retained storage type.", nameof(types));
        }
        References = new string[references.Count];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < columns.Count; index++)
        {
            _ = SqlMappingText.Identifier(columns[index]);
            if (!names.Add(columns[index]))
            {
                throw new ArgumentException("Mapped column names must be unique.", nameof(columns));
            }
            Columns[index] = columns[index];
            Types[index] = types[index];
        }
        if (!names.Contains(key))
        {
            throw new ArgumentException("The primary key must be a mapped column.", nameof(key));
        }
        for (int index = 0; index < Columns.Length; index++)
        {
            if (string.Equals(Columns[index], key, StringComparison.OrdinalIgnoreCase) &&
                Types[index] is DatabaseType.Binary or DatabaseType.Float32 or DatabaseType.Float64 or
                    DatabaseType.DateTime or DatabaseType.DateTimeOffset)
            {
                throw new NotSupportedException("SQL mapping keys cannot use binary, floating-point, DateTime or DateTimeOffset storage: SQL predicate equality does not identify their encoded keys reliably. Use an application-assigned integer, Guid, string or another supported scalar key.");
            }
        }
        for (int index = 0; index < references.Count; index++)
        {
            _ = SqlMappingText.Identifier(references[index]);
            References[index] = references[index];
        }
    }

    internal string Table { get; }
    internal string Key { get; }
    internal string[] Columns { get; }
    internal DatabaseType[] Types { get; }
    internal string[] References { get; }
}
