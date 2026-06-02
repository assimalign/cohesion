using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides an in-memory HTTP session implementation.
/// </summary>
/// <remarks>
/// The store is process-local and not persisted. <see cref="IsAvailable"/>
/// follows the conventional <c>ISession</c> contract: it is
/// <see langword="false"/> until <see cref="LoadAsync"/> has completed, after
/// which the session is considered ready. The store itself imposes no load
/// step, so reads and writes succeed before <see cref="LoadAsync"/> is called;
/// <see cref="IsAvailable"/> simply reports whether a load has occurred.
/// </remarks>
public sealed class HttpSession : IHttpSession
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private bool _isAvailable;

    /// <summary>
    /// Initializes a new session.
    /// </summary>
    /// <param name="id">The optional session identifier.</param>
    public HttpSession(string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    }

    /// <inheritdoc />
    public bool IsAvailable => _isAvailable;

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IEnumerable<string> Keys => _values.Keys;

    /// <inheritdoc />
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isAvailable = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value)
    {
        return _values.TryGetValue(key, out value);
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _values.Remove(key);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _values.Clear();
    }
}
