using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One page of the app registrations an <see cref="IIdentityClientRegistry"/> lists.
/// </summary>
/// <remarks>
/// Only a <see langword="null"/> <see cref="Continuation"/> ends an enumeration: a page may hold
/// fewer registrations than the limit asked for, or none, and still carry one.
/// </remarks>
public sealed class IdentityClientPage
{
    /// <summary>
    /// The number of registrations a page holds when a caller does not say.
    /// </summary>
    public const int DefaultLimit = IdentityHubRecordQuery.DefaultLimit;

    /// <summary>
    /// The most registrations a page holds.
    /// </summary>
    public const int MaxLimit = IdentityHubRecordQuery.MaxLimit;

    /// <summary>
    /// Initializes a page.
    /// </summary>
    /// <param name="clients">The page's registrations. The list is copied.</param>
    /// <param name="continuation">
    /// The token that continues the enumeration after this page, or <see langword="null"/> when
    /// this is the last page.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="clients"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="clients"/> holds a <see langword="null"/> registration, or
    /// <paramref name="continuation"/> is empty.
    /// </exception>
    public IdentityClientPage(IEnumerable<IdentityClient> clients, string? continuation)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (continuation is { Length: 0 })
        {
            throw new ArgumentException(ErrorMessages.PageContinuationEmpty, nameof(continuation));
        }

        var snapshot = new List<IdentityClient>();
        foreach (IdentityClient client in clients)
        {
            if (client is null)
            {
                throw new ArgumentException(ErrorMessages.PageClientRequired, nameof(clients));
            }

            snapshot.Add(client);
        }

        Clients = Array.AsReadOnly(snapshot.ToArray());
        Continuation = continuation;
    }

    /// <summary>
    /// Gets the page's registrations.
    /// </summary>
    public IReadOnlyList<IdentityClient> Clients { get; }

    /// <summary>
    /// Gets the token that continues the enumeration after this page, or <see langword="null"/>
    /// when this is the last page.
    /// </summary>
    public string? Continuation { get; }
}
