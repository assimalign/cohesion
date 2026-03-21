using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides an in-memory HTTP session implementation.
/// </summary>
public sealed class HttpSession : IHttpSession
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new session.
    /// </summary>
    /// <param name="id">The optional session identifier.</param>
    public HttpSession(string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Attempts to retrieve a UTF-8 string value from the session.
    /// </summary>
    /// <param name="key">The session key.</param>
    /// <param name="value">The resolved string value.</param>
    /// <returns><see langword="true"/> when the value was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetString(string key, [NotNullWhen(true)] out string? value)
    {
        if (_values.TryGetValue(key, out byte[]? bytes))
        {
            value = Encoding.UTF8.GetString(bytes);
            return true;
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
    }

    /// <summary>
    /// Stores a UTF-8 string value in the session.
    /// </summary>
    /// <param name="key">The session key.</param>
    /// <param name="value">The string value.</param>
    public void SetString(string key, string value)
    {
        Set(key, Encoding.UTF8.GetBytes(value));
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
