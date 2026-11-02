using System;
using System.Threading;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The default <see cref="IWebApplicationSetting{TValue}"/>: a named value replaced atomically.
/// </summary>
/// <typeparam name="TValue">The type of the setting's value.</typeparam>
public sealed class WebApplicationSetting<TValue> : IWebApplicationSetting<TValue>
{
    // The value lives in a box so that one reference swap replaces it whatever TValue is.
    private Box _box;

    private WebApplicationSetting(string name, TValue value)
    {
        Name = name;
        _box = new Box(value);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public TValue Value => Volatile.Read(ref _box).Value;

    /// <summary>
    /// Creates a setting with its initial value.
    /// </summary>
    /// <param name="name">The setting's name, unique within the application.</param>
    /// <param name="value">The initial value.</param>
    /// <returns>The setting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public static WebApplicationSetting<TValue> Create(string name, TValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new WebApplicationSetting<TValue>(name, value);
    }

    /// <inheritdoc/>
    public void Set(TValue value)
    {
        Volatile.Write(ref _box, new Box(value));
    }

    private sealed class Box
    {
        public Box(TValue value)
        {
            Value = value;
        }

        public TValue Value { get; }
    }
}
