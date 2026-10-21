using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a detected integrity violation in a storage file, such as a page
/// whose checksum does not match its content or a malformed file header.
/// </summary>
public sealed class StorageCorruptionException : StorageException
{
    /// <summary>Identifies a detected storage integrity violation.</summary>
    public const string ErrorCode = "COHDBS003";

    /// <summary>Gets the stable diagnostic code for storage corruption.</summary>
    public string Code => ErrorCode;

    /// <summary>
    /// Initializes a new <see cref="StorageCorruptionException"/> with the specified message.
    /// </summary>
    /// <param name="message">A message describing the corruption.</param>
    public StorageCorruptionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="StorageCorruptionException"/> for a specific page.
    /// </summary>
    /// <param name="pageId">The corrupted page.</param>
    /// <param name="message">A message describing the corruption.</param>
    public StorageCorruptionException(PageId pageId, string message)
        : base(message)
    {
        PageId = pageId;
    }

    /// <summary>Initializes a page-scoped integrity failure with its underlying cause.</summary>
    /// <param name="pageId">The corrupted page.</param>
    /// <param name="message">A message describing the corruption.</param>
    /// <param name="inner">The underlying failure, such as an unreadable part of the page.</param>
    public StorageCorruptionException(PageId pageId, string message, Exception inner)
        : base(message, inner)
    {
        PageId = pageId;
    }

    /// <summary>
    /// Gets the identifier of the corrupted page, when the corruption is page-scoped.
    /// </summary>
    public PageId? PageId { get; }

    /// <summary>Finds storage corruption in an exception, its inner chain, or an aggregate.</summary>
    /// <param name="error">The failure to search, or null.</param>
    /// <returns>The first storage corruption found, or null when none is found.</returns>
    public static StorageCorruptionException? Find(Exception? error)
    {
        if (error is StorageCorruptionException found) { return found; }
        if (error is null || (error.InnerException is null && error is not AggregateException)) { return null; }
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var next))
        {
            if (!visited.Add(next)) { continue; }
            if (next is StorageCorruptionException corruption) { return corruption; }
            if (next is AggregateException aggregate)
            {
                for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (next.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
        return null;
    }
}
