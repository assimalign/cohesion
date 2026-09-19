using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

/// <summary>Builds immutable entity SELECT queries restricted to the engine's executable SQL dialect.</summary>
/// <typeparam name="TEntity">The materialized entity type.</typeparam>
/// <remarks>Projection is the retained entity shape. Raw expressions, joins, grouping, subqueries,
/// set operations, windows and DDL have no entry points on this builder.</remarks>
public sealed class SqlQuery<TEntity> where TEntity : class
{
    private readonly string _table;
    private readonly IReadOnlyList<string> _columns;
    private readonly IReadOnlyList<DatabaseType> _types;
    private readonly Func<IReadOnlyList<object?>, TEntity> _reader;
    private readonly SqlPredicate<TEntity>? _predicate;
    private readonly IReadOnlyList<(string Name, bool Descending)> _order;
    private readonly bool _distinct;
    private readonly int? _take;
    private readonly int? _skip;

    internal SqlQuery(string table, IReadOnlyList<string> columns, IReadOnlyList<DatabaseType> types, Func<IReadOnlyList<object?>, TEntity> reader)
        : this(table, columns, types, reader, null, Array.Empty<(string, bool)>(), false, null, null) { }

    private SqlQuery(string table, IReadOnlyList<string> columns, IReadOnlyList<DatabaseType> types, Func<IReadOnlyList<object?>, TEntity> reader,
        SqlPredicate<TEntity>? predicate, IReadOnlyList<(string Name, bool Descending)> order,
        bool distinct, int? take, int? skip)
        => (_table, _columns, _types, _reader, _predicate, _order, _distinct, _take, _skip) =
            (table, columns, types, reader, predicate, order, distinct, take, skip);

    /// <summary>Adds a predicate, combined with an existing predicate using AND.</summary>
    /// <param name="predicate">The typed predicate.</param>
    /// <returns>A new query.</returns>
    /// <exception cref="ArgumentNullException">The predicate is null.</exception>
    public SqlQuery<TEntity> Where(SqlPredicate<TEntity> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(_table, _columns, _types, _reader, _predicate is null ? predicate : _predicate.And(predicate), _order, _distinct, _take, _skip);
    }

    /// <summary>Starts ordering by a retained column, replacing any earlier ordering.</summary>
    /// <typeparam name="TValue">The declared column value type.</typeparam>
    /// <param name="column">The ordering column.</param>
    /// <param name="descending">Whether to order descending.</param>
    /// <returns>A new query.</returns>
    /// <exception cref="ArgumentNullException">The column is null.</exception>
    /// <exception cref="ArgumentException">The column is not part of this mapping.</exception>
    public SqlQuery<TEntity> OrderBy<TValue>(SqlColumn<TEntity, TValue> column, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(column);
        SqlPredicate<TEntity>.ValidateColumn(column.TableName, column.ColumnName, _table, _columns);
        return new(_table, _columns, _types, _reader, _predicate, new[] { (column.ColumnName, descending) }, _distinct, _take, _skip);
    }

    /// <summary>Adds a tie-breaking ordering column.</summary>
    /// <typeparam name="TValue">The declared column value type.</typeparam>
    /// <param name="column">The ordering column.</param>
    /// <param name="descending">Whether to order descending.</param>
    /// <returns>A new query.</returns>
    /// <exception cref="ArgumentNullException">The column is null.</exception>
    /// <exception cref="ArgumentException">The column is not part of this mapping.</exception>
    /// <exception cref="InvalidOperationException">No preceding OrderBy exists.</exception>
    public SqlQuery<TEntity> ThenBy<TValue>(SqlColumn<TEntity, TValue> column, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (_order.Count == 0)
        {
            throw new InvalidOperationException("ThenBy requires a preceding OrderBy.");
        }
        SqlPredicate<TEntity>.ValidateColumn(column.TableName, column.ColumnName, _table, _columns);
        var order = new (string, bool)[_order.Count + 1];
        for (int index = 0; index < _order.Count; index++)
        {
            order[index] = _order[index];
        }
        order[^1] = (column.ColumnName, descending);
        return new(_table, _columns, _types, _reader, _predicate, order, _distinct, _take, _skip);
    }

    /// <summary>Requests distinct retained entity rows.</summary>
    /// <returns>A new query.</returns>
    public SqlQuery<TEntity> Distinct() => new(_table, _columns, _types, _reader, _predicate, _order, true, _take, _skip);

    /// <summary>Sets a nonnegative maximum result count using LIMIT.</summary>
    /// <param name="count">The maximum result count.</param>
    /// <returns>A new query.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public SqlQuery<TEntity> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new(_table, _columns, _types, _reader, _predicate, _order, _distinct, count, _skip);
    }

    /// <summary>Sets the nonnegative number of rows to skip using OFFSET.</summary>
    /// <param name="count">The number of rows to skip.</param>
    /// <returns>A new query.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public SqlQuery<TEntity> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new(_table, _columns, _types, _reader, _predicate, _order, _distinct, _take, count);
    }

    /// <summary>Compiles this query to a fresh parameterized command.</summary>
    /// <returns>A command independent of this immutable query.</returns>
    /// <exception cref="ArgumentException">A predicate column is not part of this mapping or its value has a different retained storage type.</exception>
    /// <exception cref="NotSupportedException">A non-null comparison targets binary or floating-point data outside the engine's complete comparison domain.</exception>
    public SqlCommand ToCommand()
    {
        var command = new SqlCommand("SELECT");
        var text = new StringBuilder(_distinct ? "SELECT DISTINCT " : "SELECT ");
        for (int index = 0; index < _columns.Count; index++)
        {
            if (index > 0)
            {
                text.Append(", ");
            }
            text.Append(SqlMappingText.Identifier(_columns[index]));
        }
        text.Append(" FROM ").Append(SqlMappingText.Identifier(_table));
        if (_predicate is not null)
        {
            text.Append(" WHERE ").Append(_predicate.Compile(command, _table, _columns, _types));
        }
        for (int index = 0; index < _order.Count; index++)
        {
            text.Append(index == 0 ? " ORDER BY " : ", ");
            text.Append(SqlMappingText.Identifier(_order[index].Name)).Append(_order[index].Descending ? " DESC" : " ASC");
        }
        if (_take.HasValue)
        {
            text.Append(" LIMIT ").Append(_take.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (_skip.HasValue)
        {
            text.Append(" OFFSET ").Append(_skip.Value.ToString(CultureInfo.InvariantCulture));
        }
        command.CommandText = text.Append(';').ToString();
        return command;
    }

    internal TEntity Read(IReadOnlyList<object?> values) => _reader(values);
}
