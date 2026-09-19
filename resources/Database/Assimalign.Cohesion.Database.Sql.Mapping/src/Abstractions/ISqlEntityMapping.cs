using System.Collections.Generic;

using Assimalign.Cohesion.Database.Mapping;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

/// <summary>Describes a statically generated mapping of one retained SQL schema table.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The immutable application-assigned primary key.</typeparam>
/// <typeparam name="TSnapshot">The detached generated snapshot.</typeparam>
/// <remarks>Generated implementations derive these declarations from SqlSchema. Hand-written implementations
/// must provide equivalent ordered columns and immutable snapshots; no schema discovery occurs at runtime.</remarks>
public interface ISqlEntityMapping<TEntity, TKey, TSnapshot> : IEntityMapper<TEntity, TKey, TSnapshot>,
    IEntityReader<TEntity, IReadOnlyList<object?>>, IEntityWriter<TEntity, IList<object?>>
    where TEntity : class where TKey : notnull
{
    /// <summary>Gets the retained table name.</summary>
    string TableName { get; }

    /// <summary>Gets the retained column names in materialization order.</summary>
    IReadOnlyList<string> ColumnNames { get; }

    /// <summary>Gets storage types in the same order as the retained column names.</summary>
    IReadOnlyList<DatabaseType> ColumnTypes { get; }

    /// <summary>Gets the primary-key column name.</summary>
    string KeyColumnName { get; }

    /// <summary>Gets the referenced table names used to order parent and child writes.</summary>
    IReadOnlyList<string> ReferencedTables { get; }

    /// <summary>Copies detached snapshot values to the retained column order.</summary>
    /// <param name="snapshot">The snapshot to copy.</param>
    /// <param name="target">The writable destination, with one slot per column.</param>
    /// <exception cref="System.ArgumentNullException">The target is null, or a reference-type snapshot is null.</exception>
    /// <exception cref="System.ArgumentException">The target's count does not match the retained column count.</exception>
    /// <exception cref="System.NotSupportedException">The target does not permit assigning values.</exception>
    void WriteSnapshot(TSnapshot snapshot, IList<object?> target);
}
