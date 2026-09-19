using System;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

/// <summary>Identifies a retained column and builds type-checked scalar predicates.</summary>
/// <typeparam name="TEntity">The mapped entity.</typeparam>
/// <typeparam name="TValue">The declared member value type.</typeparam>
public sealed class SqlColumn<TEntity, TValue> where TEntity : class
{
    /// <summary>Creates a column token, normally emitted by the schema generator.</summary>
    /// <param name="tableName">The retained table name.</param>
    /// <param name="columnName">The retained column name.</param>
    /// <exception cref="ArgumentException">An identifier is empty or contains a null character or embedded double quote.</exception>
    public SqlColumn(string tableName, string columnName)
    {
        _ = SqlMappingText.Identifier(tableName);
        _ = SqlMappingText.Identifier(columnName);
        TableName = tableName;
        ColumnName = columnName;
    }

    /// <summary>Gets the retained table name.</summary>
    public string TableName { get; }

    /// <summary>Gets the retained column name.</summary>
    public string ColumnName { get; }

    /// <summary>Compares the column for equality; a null value compiles to IS NULL.</summary>
    /// <param name="value">The comparison value.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="ArgumentException">The value is unsupported or a nonfinite floating-point number.</exception>
    public SqlPredicate<TEntity> Equal(TValue value) => Compare("=", value);

    /// <summary>Compares the column for inequality; a null value compiles to IS NOT NULL.</summary>
    /// <param name="value">The comparison value.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="ArgumentException">The value is unsupported or a nonfinite floating-point number.</exception>
    public SqlPredicate<TEntity> NotEqual(TValue value) => Compare("<>", value);

    /// <summary>Compares the column to a smaller non-null value.</summary>
    /// <param name="value">The comparison value.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="ArgumentException">The value is null, unsupported or a nonfinite floating-point number.</exception>
    public SqlPredicate<TEntity> GreaterThan(TValue value) => Compare(">", value);

    /// <summary>Compares the column to a smaller or equal non-null value.</summary>
    /// <param name="value">The comparison value.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="ArgumentException">The value is null, unsupported or a nonfinite floating-point number.</exception>
    public SqlPredicate<TEntity> GreaterThanOrEqual(TValue value) => Compare(">=", value);

    /// <summary>Compares the column to a larger non-null value.</summary>
    /// <param name="value">The comparison value.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="ArgumentException">The value is null, unsupported or a nonfinite floating-point number.</exception>
    public SqlPredicate<TEntity> LessThan(TValue value) => Compare("<", value);

    /// <summary>Compares the column to a larger or equal non-null value.</summary>
    /// <param name="value">The comparison value.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="ArgumentException">The value is null, unsupported or a nonfinite floating-point number.</exception>
    public SqlPredicate<TEntity> LessThanOrEqual(TValue value) => Compare("<=", value);

    /// <summary>Tests whether the column is SQL NULL.</summary>
    /// <returns>The predicate.</returns>
    public SqlPredicate<TEntity> IsNull() => new(TableName, ColumnName, "IS NULL", null);

    /// <summary>Tests whether the column is not SQL NULL.</summary>
    /// <returns>The predicate.</returns>
    public SqlPredicate<TEntity> IsNotNull() => new(TableName, ColumnName, "IS NOT NULL", null);

    private SqlPredicate<TEntity> Compare(string operation, TValue value)
    {
        if (value is null)
        {
            return operation switch
            {
                "=" => IsNull(),
                "<>" => IsNotNull(),
                _ => throw new ArgumentException("Ordered comparison requires a non-null value; use IsNull or IsNotNull.", nameof(value)),
            };
        }
        return new(TableName, ColumnName, operation, SqlMappingText.Value(value));
    }
}
