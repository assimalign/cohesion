using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Mapping;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

internal sealed class SqlEntityChangeWriter<TEntity, TKey, TSnapshot> : IEntityChangeWriter<TKey, TSnapshot, SqlMappingTransaction>
    where TEntity : class where TKey : notnull
{
    private readonly ISqlEntityMapping<TEntity, TKey, TSnapshot> _mapping;
    private readonly SqlMappingMetadata _metadata;

    internal SqlEntityChangeWriter(ISqlEntityMapping<TEntity, TKey, TSnapshot> mapping)
    {
        _mapping = mapping;
        _metadata = new(mapping.TableName, mapping.ColumnNames, mapping.ColumnTypes, mapping.KeyColumnName, mapping.ReferencedTables);
    }

    public ValueTask ApplyAsync(SqlMappingTransaction transaction, EntityChange<TKey, TSnapshot> change,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string table = SqlMappingText.Identifier(_metadata.Table);
        string key = SqlMappingText.Identifier(_metadata.Key);
        var command = new SqlCommand("DELETE FROM " + table + " WHERE " + key + " = @key;");
        if (change.Kind == EntityChangeKind.Deleted)
        {
            command.WithParameter("key", SqlMappingText.Value(change.Key));
        }
        else
        {
            var values = new object?[_metadata.Columns.Length];
            _mapping.WriteSnapshot(change.Current, values);
            var text = new StringBuilder();
            if (change.Kind == EntityChangeKind.Added)
            {
                text.Append("INSERT INTO ").Append(table).Append(" (");
                for (int index = 0; index < values.Length; index++)
                {
                    if (index > 0)
                    {
                        text.Append(", ");
                    }
                    text.Append(SqlMappingText.Identifier(_metadata.Columns[index]));
                }
                text.Append(") VALUES (");
                for (int index = 0; index < values.Length; index++)
                {
                    if (index > 0)
                    {
                        text.Append(", ");
                    }
                    string parameter = "p" + index.ToString(CultureInfo.InvariantCulture);
                    text.Append('@').Append(parameter);
                    command.WithParameter(parameter, SqlMappingText.Value(values[index]));
                }
                text.Append(");");
            }
            else if (change.Kind == EntityChangeKind.Modified)
            {
                text.Append("UPDATE ").Append(table).Append(" SET ");
                bool first = true;
                for (int index = 0; index < values.Length; index++)
                {
                    if (string.Equals(_metadata.Columns[index], _metadata.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!first)
                    {
                        text.Append(", ");
                    }
                    first = false;
                    string parameter = "p" + index.ToString(CultureInfo.InvariantCulture);
                    text.Append(SqlMappingText.Identifier(_metadata.Columns[index])).Append(" = @").Append(parameter);
                    command.WithParameter(parameter, SqlMappingText.Value(values[index]));
                }
                if (first)
                {
                    text.Append(key).Append(" = @key");
                }
                text.Append(" WHERE ").Append(key).Append(" = @key;");
                command.WithParameter("key", SqlMappingText.Value(change.Key));
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(change), "Unknown entity change kind.");
            }
            command.CommandText = text.ToString();
        }
        transaction.Stage(_metadata, change.Kind, command);
        return ValueTask.CompletedTask;
    }
}
