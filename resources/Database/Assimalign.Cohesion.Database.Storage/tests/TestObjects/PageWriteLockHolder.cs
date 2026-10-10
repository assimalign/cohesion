using System;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A storage bracket that write-locks every page of a storage: until it is disposed, any other
/// bracket that touches a page fails with <see cref="StorageTransactionException"/>, and once it is
/// disposed (rolled back, changing nothing) the same work succeeds. Engine tests use it as a
/// transient, or with a long hold a persistent, failure of a rollback's undo.
/// </summary>
/// <remarks>
/// Until #1252 those tests failed one journal write of the undo instead. A journal write is now a
/// drain of the journal's append buffer, and a failed one takes the storage offline, so it is no
/// longer a failure an undo retry can outlive.
/// </remarks>
public sealed class PageWriteLockHolder : IDisposable
{
    private readonly StorageTransaction _bracket;

    private PageWriteLockHolder(StorageTransaction bracket, int pages)
    {
        _bracket = bracket;
        Pages = pages;
    }

    /// <summary>Gets the number of pages the holder locked.</summary>
    public int Pages { get; }

    /// <summary>
    /// Begins a bracket on <paramref name="storage"/> and write-locks every page after the file
    /// header.
    /// </summary>
    /// <param name="storage">The storage whose pages to lock.</param>
    /// <returns>The holder; dispose it to release the pages.</returns>
    public static PageWriteLockHolder LockEveryPage(Storage storage)
    {
        var bracket = storage.BeginTransaction();
        long count = storage.PageManager.PageCount;
        for (long page = 1; page < count; page++)
        {
            using var handle = storage.OpenPageForWrite(bracket, (PageId)page);
        }

        return new PageWriteLockHolder(bracket, (int)(count - 1));
    }

    /// <summary>Rolls the bracket back, which releases every page it locked.</summary>
    public void Dispose()
    {
        if (_bracket.IsActive)
        {
            _bracket.Rollback();
        }
    }
}
