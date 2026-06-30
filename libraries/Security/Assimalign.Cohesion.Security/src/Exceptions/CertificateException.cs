using System;

namespace Assimalign.Cohesion.Security;

/// <summary>
/// The exception thrown when a certificate cannot be loaded or processed. Serves
/// as the area-scoped exception root for the Cohesion Security library.
/// </summary>
public class CertificateException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CertificateException"/> class with a message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CertificateException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CertificateException"/> class with a message
    /// and a reference to the underlying cause.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of this exception.</param>
    public CertificateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
