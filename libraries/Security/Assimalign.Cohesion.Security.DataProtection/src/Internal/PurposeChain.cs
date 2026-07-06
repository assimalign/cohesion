using System;
using System.Buffers.Binary;
using System.Text;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Encodes a protector's purpose chain into the HKDF <c>info</c> parameter that binds a
/// derived subkey to that exact chain. The encoding is unambiguous — each purpose is
/// length-prefixed — so <c>["ab", "c"]</c> and <c>["a", "bc"]</c> derive different subkeys,
/// and a fixed context label domain-separates this derivation from any unrelated use of the
/// same master key.
/// </summary>
internal static class PurposeChain
{
    // A version-stamped context so the derivation can evolve without silently colliding.
    private static ReadOnlySpan<byte> Context => "Assimalign.Cohesion.Security.DataProtection.v1"u8;

    /// <summary>
    /// Builds the HKDF <c>info</c> bytes for <paramref name="purposes"/>:
    /// <c>context ‖ (uint32-BE length ‖ UTF-8 bytes) for each purpose</c>.
    /// </summary>
    /// <param name="purposes">The full purpose chain, discriminator first.</param>
    /// <returns>The deterministic <c>info</c> bytes.</returns>
    public static byte[] BuildInfo(string[] purposes)
    {
        int total = Context.Length;
        for (int i = 0; i < purposes.Length; i++)
        {
            total += sizeof(uint) + Encoding.UTF8.GetByteCount(purposes[i]);
        }

        byte[] info = new byte[total];
        Span<byte> cursor = info;

        Context.CopyTo(cursor);
        cursor = cursor.Slice(Context.Length);

        for (int i = 0; i < purposes.Length; i++)
        {
            int written = Encoding.UTF8.GetBytes(purposes[i], cursor.Slice(sizeof(uint)));
            BinaryPrimitives.WriteUInt32BigEndian(cursor, (uint)written);
            cursor = cursor.Slice(sizeof(uint) + written);
        }

        return info;
    }
}
