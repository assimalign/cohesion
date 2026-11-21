using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One page of the identity objects an <see cref="IIdentityObjectRegistry{TObject, TId}"/> lists.
/// </summary>
/// <remarks>
/// Only a <see langword="null"/> <see cref="Continuation"/> ends an enumeration: a page may hold
/// fewer objects than the limit asked for, or none, and still carry one.
/// </remarks>
/// <typeparam name="TObject">The identity object the page holds.</typeparam>
public sealed class IdentityObjectPage<TObject>
    where TObject : class
{
    /// <summary>
    /// Initializes a page.
    /// </summary>
    /// <param name="items">The page's objects. The list is copied.</param>
    /// <param name="continuation">
    /// The token that continues the enumeration after this page, or <see langword="null"/> when
    /// this is the last page.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="items"/> holds a <see langword="null"/> object, or
    /// <paramref name="continuation"/> is empty.
    /// </exception>
    public IdentityObjectPage(IEnumerable<TObject> items, string? continuation)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (continuation is { Length: 0 })
        {
            throw new ArgumentException(ErrorMessages.PageContinuationEmpty, nameof(continuation));
        }

        var snapshot = new List<TObject>();
        foreach (TObject item in items)
        {
            if (item is null)
            {
                throw new ArgumentException(ErrorMessages.PageObjectRequired, nameof(items));
            }

            snapshot.Add(item);
        }

        Items = Array.AsReadOnly(snapshot.ToArray());
        Continuation = continuation;
    }

    /// <summary>
    /// Gets the page's objects.
    /// </summary>
    public IReadOnlyList<TObject> Items { get; }

    /// <summary>
    /// Gets the token that continues the enumeration after this page, or <see langword="null"/>
    /// when this is the last page.
    /// </summary>
    public string? Continuation { get; }
}
