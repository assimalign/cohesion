using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Mapping;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

/// <summary>Composes generated SQL mappings with queries and the shared unit of work.</summary>
public static class SqlMapping
{
    /// <summary>Registers a retained mapping with SQL change staging.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The immutable primary-key type.</typeparam>
    /// <typeparam name="TSnapshot">The generated snapshot type.</typeparam>
    /// <param name="unitOfWork">The shared save scope.</param>
    /// <param name="mapping">The generated retained mapping.</param>
    /// <param name="keyComparer">An optional comparer matching the database's primary-key equality.
    /// The default follows binary string collation and supported scalar identity; supply the matching
    /// comparer when the database uses a different default string collation.</param>
    /// <returns>The registered identity and change-tracking set.</returns>
    /// <exception cref="ArgumentNullException">The scope or mapping is null.</exception>
    /// <exception cref="ArgumentException">Mapping metadata is invalid.</exception>
    /// <exception cref="InvalidOperationException">A save is active or the unit of work has an unresolved commit outcome.</exception>
    /// <exception cref="NotSupportedException">The primary-key storage type cannot be addressed reliably by SQL equality.</exception>
    public static ITrackedEntities<TEntity, TKey> Register<TEntity, TKey, TSnapshot>(
        IMappingUnitOfWork<SqlMappingTransaction> unitOfWork, ISqlEntityMapping<TEntity, TKey, TSnapshot> mapping,
        IEqualityComparer<TKey>? keyComparer = null) where TEntity : class where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(mapping);
        return unitOfWork.Register(mapping, new SqlEntityChangeWriter<TEntity, TKey, TSnapshot>(mapping), keyComparer);
    }

    /// <summary>Starts a typed SELECT builder for the mapping's complete retained shape.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The primary-key type.</typeparam>
    /// <typeparam name="TSnapshot">The snapshot type.</typeparam>
    /// <param name="mapping">The generated retained mapping.</param>
    /// <returns>An immutable query builder.</returns>
    /// <exception cref="ArgumentNullException">The mapping is null.</exception>
    /// <exception cref="ArgumentException">Mapping metadata is invalid.</exception>
    /// <exception cref="NotSupportedException">The primary-key storage type cannot be addressed reliably by SQL equality.</exception>
    public static SqlQuery<TEntity> Query<TEntity, TKey, TSnapshot>(ISqlEntityMapping<TEntity, TKey, TSnapshot> mapping)
        where TEntity : class where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var metadata = new SqlMappingMetadata(mapping.TableName, mapping.ColumnNames, mapping.ColumnTypes, mapping.KeyColumnName, mapping.ReferencedTables);
        return new(metadata.Table, metadata.Columns, metadata.Types, mapping.Read);
    }
}
