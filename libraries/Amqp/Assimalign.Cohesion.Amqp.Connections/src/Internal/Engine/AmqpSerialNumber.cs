namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Arithmetic on the 32-bit serial numbers AMQP uses for transfer ids, delivery ids and delivery counts,
/// which wrap around (AMQP 1.0 Part 2, section 2.8.9; RFC 1982).
/// </summary>
internal static class AmqpSerialNumber
{
    /// <summary>
    /// Determines whether <paramref name="value"/> lies in the inclusive range that starts at
    /// <paramref name="first"/> and runs forward to <paramref name="last"/>.
    /// </summary>
    public static bool IsInRange(uint value, uint first, uint last)
    {
        return unchecked(value - first) <= unchecked(last - first);
    }

    /// <summary>
    /// Returns the signed distance from <paramref name="from"/> forward to <paramref name="to"/>.
    /// </summary>
    public static int Difference(uint to, uint from)
    {
        return unchecked((int)(to - from));
    }
}
