namespace Assimalign.Cohesion.Web;

/// <summary>
/// A setting whose value is a <typeparamref name="TValue"/>.
/// </summary>
/// <remarks>
/// A component reads <see cref="Value"/> each time it needs it, so a value <see cref="Set"/> replaces
/// takes effect from the component's next read. Values should be immutable, so a read never sees one
/// half changed.
/// </remarks>
/// <typeparam name="TValue">The type of the setting's value.</typeparam>
public interface IWebApplicationSetting<TValue> : IWebApplicationSetting
{
    /// <summary>
    /// Gets the value in effect.
    /// </summary>
    TValue Value { get; }

    /// <summary>
    /// Replaces the value in effect.
    /// </summary>
    /// <param name="value">The new value.</param>
    /// <exception cref="System.ArgumentException">The value is not valid for the setting.</exception>
    void Set(TValue value);
}
