using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Typed get/set helpers over the binary <see cref="IHttpSession"/> store, so
/// callers can persist common scalar types without hand-encoding bytes.
/// </summary>
/// <remarks>
/// Strings use UTF-8; 32-bit integers use a fixed big-endian (network-order)
/// encoding so the byte layout is stable and interoperable with any other
/// store that follows the same convention. The helpers are extension members
/// on <see cref="IHttpSession"/> so they apply to every session implementation,
/// not just the in-memory <see cref="HttpSession"/>.
/// </remarks>
public static class HttpSessionExtensions
{
    extension(IHttpSession session)
    {
        /// <summary>
        /// Stores a UTF-8 string value in the session.
        /// </summary>
        /// <param name="key">The session key.</param>
        /// <param name="value">The string value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
        public void SetString(string key, string value)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(value);

            session.Set(key, Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        /// Retrieves a UTF-8 string value, or <see langword="null"/> when the
        /// key is not present.
        /// </summary>
        /// <param name="key">The session key.</param>
        /// <returns>The decoded string, or <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
        public string? GetString(string key)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(key);

            return session.TryGetValue(key, out byte[]? bytes)
                ? Encoding.UTF8.GetString(bytes)
                : null;
        }

        /// <summary>
        /// Attempts to retrieve a UTF-8 string value from the session.
        /// </summary>
        /// <param name="key">The session key.</param>
        /// <param name="value">The decoded string when found.</param>
        /// <returns><see langword="true"/> when the value was found; otherwise <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
        public bool TryGetString(string key, [NotNullWhen(true)] out string? value)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(key);

            if (session.TryGetValue(key, out byte[]? bytes))
            {
                value = Encoding.UTF8.GetString(bytes);
                return true;
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Stores a 32-bit integer value using a big-endian encoding.
        /// </summary>
        /// <param name="key">The session key.</param>
        /// <param name="value">The integer value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
        public void SetInt32(string key, int value)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(key);

            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            session.Set(key, bytes);
        }

        /// <summary>
        /// Retrieves a 32-bit integer value, or <see langword="null"/> when the
        /// key is absent or the stored value is not exactly four bytes.
        /// </summary>
        /// <param name="key">The session key.</param>
        /// <returns>The integer value, or <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
        public int? GetInt32(string key)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(key);

            return session.TryGetValue(key, out byte[]? bytes) && bytes.Length == sizeof(int)
                ? BinaryPrimitives.ReadInt32BigEndian(bytes)
                : null;
        }

        /// <summary>
        /// Attempts to retrieve a 32-bit integer value from the session.
        /// </summary>
        /// <param name="key">The session key.</param>
        /// <param name="value">The integer value when found and well-formed.</param>
        /// <returns><see langword="true"/> when a four-byte value was found; otherwise <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
        public bool TryGetInt32(string key, out int value)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(key);

            if (session.TryGetValue(key, out byte[]? bytes) && bytes.Length == sizeof(int))
            {
                value = BinaryPrimitives.ReadInt32BigEndian(bytes);
                return true;
            }

            value = 0;
            return false;
        }
    }
}
