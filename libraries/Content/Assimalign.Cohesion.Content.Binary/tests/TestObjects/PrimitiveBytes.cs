namespace Assimalign.Cohesion.Content.Binary.Tests;

internal static class PrimitiveBytes
{
    // All fixtures are generated in code; no downloaded media or external corpus is used.
    internal static byte[] Sequence(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)i;
        }
        return data;
    }
}
