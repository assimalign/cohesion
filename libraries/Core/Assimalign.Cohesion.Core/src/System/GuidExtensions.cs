using System;
using System.Security.Cryptography;
using System.Text;

namespace System;

public static class GuidExtension
{
    private static readonly Guid DefaultNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    extension(Guid guid)
    {
        /// <summary>
        /// Generates the Guid from the specified string value using a deterministic algorithm based on SHA1 hashing. The same input value and 
        /// namespace will always produce the same GUID, making it suitable for scenarios where consistent identifiers are required across different systems or sessions.
        /// </summary>
        /// <param name="value">The string value to convert into a deterministic GUID.</param>
        /// <param name="namespaceId">The namespace UUID to use for generation. If <see langword="null"/>, a default namespace is used.</param>
        /// <returns>A deterministic GUID derived from the specified value and namespace using SHA1 hashing.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
        public static Guid AsDeterministicGuid(string value, Guid? namespaceId = null)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var ns = namespaceId ?? DefaultNamespace;

            var namespaceBytes = ns.ToByteArray();
            SwapByteOrder(namespaceBytes);

            var nameBytes = Encoding.UTF8.GetBytes(value);

            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(Combine(namespaceBytes, nameBytes));

            var newGuid = new byte[16];
            Array.Copy(hash, 0, newGuid, 0, 16);

            // Set version (5 = SHA1 name-based)
            newGuid[6] = (byte)((newGuid[6] & 0x0F) | (5 << 4));

            // Set variant (RFC 4122)
            newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);

            SwapByteOrder(newGuid);

            return new Guid(newGuid);
        }
    }

    private static byte[] Combine(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static void SwapByteOrder(byte[] guid)
    {
        static void Swap(byte[] g, int a, int b)
        {
            (g[a], g[b]) = (g[b], g[a]);
        }

        Swap(guid, 0, 3);
        Swap(guid, 1, 2);
        Swap(guid, 4, 5);
        Swap(guid, 6, 7);
    }
}