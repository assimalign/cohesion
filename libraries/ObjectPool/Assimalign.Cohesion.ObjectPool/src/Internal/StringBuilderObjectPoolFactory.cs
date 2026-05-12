using System.Text;

namespace Assimalign.Cohesion.ObjectPool.Internal;

internal class StringBuilderObjectPoolFactory : ObjectPoolFactory<StringBuilder>
{
    /// <summary>
    /// Gets or sets the initial capacity of pooled <see cref="StringBuilder"/> instances.
    /// </summary>
    /// <value>Defaults to <c>100</c>.</value>
    public int InitialCapacity { get; set; } = 100;

    /// <inheritdoc />
    public override StringBuilder Create()
    {
        return new StringBuilder(InitialCapacity);
    }
}
