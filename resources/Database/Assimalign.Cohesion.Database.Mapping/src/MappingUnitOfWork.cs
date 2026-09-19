using System;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Creates explicit mapping scopes without runtime discovery.</summary>
public static class MappingUnitOfWork
{
    /// <summary>Creates a single-caller unit of work over an atomic store adapter.</summary>
    /// <typeparam name="TTransaction">The adapter's transaction contract.</typeparam>
    /// <param name="store">The store; its lifetime remains owned by the caller.</param>
    /// <returns>A new unit of work.</returns>
    /// <exception cref="ArgumentNullException">The store is null.</exception>
    public static IMappingUnitOfWork<TTransaction> Create<TTransaction>(IMappingStore<TTransaction> store)
        where TTransaction : class, IMappingTransaction
    {
        ArgumentNullException.ThrowIfNull(store);
        return new MappingSession<TTransaction>(store);
    }
}
