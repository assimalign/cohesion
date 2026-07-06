using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// The exception thrown when a data-protection operation fails — for example when a
/// protected payload is malformed, its authentication tag does not verify, or the key
/// that produced it is unknown, revoked, or aged out of the unprotect grace window.
/// Serves as the area-scoped exception root for the Cohesion data-protection library.
/// </summary>
/// <remarks>
/// Payloads presented to <see cref="IDataProtector.Unprotect(System.ReadOnlySpan{byte})"/>
/// are untrusted input. A tampered, truncated, or foreign payload surfaces as this
/// exception rather than a low-level <see cref="System.Security.Cryptography.CryptographicException"/>,
/// so callers can catch a single area-scoped type. Messages never reveal key material or
/// plaintext; because protection is AEAD (no padding oracle), distinguishing an
/// authentication failure from a key-lifecycle failure is safe and aids operators.
/// </remarks>
public class DataProtectionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DataProtectionException"/> class with a message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public DataProtectionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DataProtectionException"/> class with a message
    /// and a reference to the underlying cause.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of this exception.</param>
    public DataProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
