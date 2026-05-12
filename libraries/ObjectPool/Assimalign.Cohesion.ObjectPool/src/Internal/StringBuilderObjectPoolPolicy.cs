using System.Text;

namespace Assimalign.Cohesion.ObjectPool.Internal;

/// <summary>
/// A policy for pooling <see cref="StringBuilder"/> instances.
/// </summary>
internal class StringBuilderObjectPoolPolicy : ObjectPoolPolicy<StringBuilder>
{
    /// <summary>
    /// Gets or sets the maximum value for <see cref="StringBuilder.Capacity"/> that is allowed to be
    /// retained, when <see cref="CanReturn(StringBuilder)"/> is invoked.
    /// </summary>
    /// <value>Defaults to <c>4096</c>.</value>
    public int MaximumRetainedCapacity { get; set; } = 4 * 1024;

    /// <inheritdoc />
    public override bool CanReturn(StringBuilder stringBuilder)
    {
        if (stringBuilder.Capacity > MaximumRetainedCapacity)
        {
            // Too big. Discard this one.
            return false;
        }

        stringBuilder.Clear();

        return true;
    }
}
