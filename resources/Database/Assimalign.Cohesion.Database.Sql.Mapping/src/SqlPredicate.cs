using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

/// <summary>Represents an immutable predicate in the engine's executable Boolean subset.</summary>
/// <typeparam name="TEntity">The mapped entity.</typeparam>
public sealed class SqlPredicate<TEntity> where TEntity : class
{
    private readonly string? _table;
    private readonly string? _column;
    private readonly string _operation;
    private readonly object? _value;
    private readonly SqlPredicate<TEntity>? _left;
    private readonly SqlPredicate<TEntity>? _right;

    internal SqlPredicate(string table, string column, string operation, object? value)
        => (_table, _column, _operation, _value) = (table, column, operation, value);

    private SqlPredicate(string operation, SqlPredicate<TEntity> left, SqlPredicate<TEntity>? right = null)
        => (_operation, _left, _right) = (operation, left, right);

    /// <summary>Requires both predicates to be true.</summary>
    /// <param name="other">The additional predicate.</param>
    /// <returns>A new combined predicate.</returns>
    /// <exception cref="ArgumentNullException">The predicate is null.</exception>
    public SqlPredicate<TEntity> And(SqlPredicate<TEntity> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new("AND", this, other);
    }

    /// <summary>Requires either predicate to be true.</summary>
    /// <param name="other">The alternative predicate.</param>
    /// <returns>A new combined predicate.</returns>
    /// <exception cref="ArgumentNullException">The predicate is null.</exception>
    public SqlPredicate<TEntity> Or(SqlPredicate<TEntity> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new("OR", this, other);
    }

    /// <summary>Negates this predicate using SQL's three-valued logic.</summary>
    /// <returns>A new negated predicate.</returns>
    public SqlPredicate<TEntity> Not() => new("NOT", this);

    internal string Compile(SqlCommand command, string table, IReadOnlyList<string> columns, IReadOnlyList<DatabaseType> types)
    {
        if (_left is not null)
        {
            string left = _left.Compile(command, table, columns, types);
            return _right is null ? "(NOT " + left + ")" :
                "(" + left + " " + _operation + " " + _right.Compile(command, table, columns, types) + ")";
        }
        int ordinal = ValidateColumn(_table!, _column!, table, columns);
        string column = SqlMappingText.Identifier(_column!);
        if (_operation is "IS NULL" or "IS NOT NULL")
        {
            return "(" + column + " " + _operation + ")";
        }
        if (SqlMappingText.ValueType(_value) != types[ordinal])
        {
            throw new ArgumentException("The comparison value does not match the retained column's storage type.");
        }
        if (types[ordinal] is DatabaseType.Binary or DatabaseType.Float32 or DatabaseType.Float64)
        {
            throw new NotSupportedException("The executable SQL comparison evaluator does not support the full binary or floating-point value domain. Use a null test, materialize the values, or choose a supported scalar column.");
        }
        string parameter = "p" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        command.Parameters.Add(parameter, SqlMappingText.Value(_value));
        return "(" + column + " " + _operation + " @" + parameter + ")";
    }

    internal static int ValidateColumn(string tableName, string columnName, string table, IReadOnlyList<string> columns)
    {
        if (string.Equals(tableName, table, StringComparison.OrdinalIgnoreCase))
        {
            for (int index = 0; index < columns.Count; index++)
            {
                if (string.Equals(columnName, columns[index], StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }
        throw new ArgumentException("The column token does not belong to this retained table mapping.");
    }
}
