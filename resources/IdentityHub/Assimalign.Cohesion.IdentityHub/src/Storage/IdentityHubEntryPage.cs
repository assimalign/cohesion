using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One page of the hub values an <see cref="IdentityHubRecordQuery"/> selects from an
/// <see cref="IdentityHubRecordSet{TRecord}"/>.
/// </summary>
/// <remarks>
/// Only a <see langword="null"/> <see cref="Continuation"/> ends an enumeration: a page may hold
/// fewer entries than the query's limit, or none, and still carry one.
/// </remarks>
/// <typeparam name="TRecord">The hub value.</typeparam>
public sealed class IdentityHubEntryPage<TRecord>
    where TRecord : notnull
{
    /// <summary>
    /// Initializes a page.
    /// </summary>
    /// <param name="entries">The page's entries. The list is copied.</param>
    /// <param name="continuation">
    /// The token that continues the enumeration after this page, or <see langword="null"/> when
    /// this is the last page.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="entries"/> holds a <see langword="null"/> entry, or
    /// <paramref name="continuation"/> is empty.
    /// </exception>
    public IdentityHubEntryPage(IEnumerable<IdentityHubEntry<TRecord>> entries, string? continuation)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (continuation is { Length: 0 })
        {
            throw new ArgumentException(ErrorMessages.PageContinuationEmpty, nameof(continuation));
        }

        var snapshot = new List<IdentityHubEntry<TRecord>>();
        foreach (IdentityHubEntry<TRecord> entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException(ErrorMessages.PageRecordRequired, nameof(entries));
            }

            snapshot.Add(entry);
        }

        Entries = Array.AsReadOnly(snapshot.ToArray());
        Continuation = continuation;
    }

    /// <summary>
    /// Gets the page's entries.
    /// </summary>
    public IReadOnlyList<IdentityHubEntry<TRecord>> Entries { get; }

    /// <summary>
    /// Gets the token that continues the enumeration after this page, or <see langword="null"/>
    /// when this is the last page.
    /// </summary>
    public string? Continuation { get; }
}
