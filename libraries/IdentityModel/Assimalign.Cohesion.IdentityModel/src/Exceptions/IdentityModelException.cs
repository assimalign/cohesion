using System;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// The area-scoped exception root for the IdentityModel family. Thrown for domain-invariant
/// violations such as invalid descriptor materialization, claim-value kind mismatches, and
/// actor-chain depth violations. Protocol validation failures are represented as result
/// values, not exceptions.
/// </summary>
public class IdentityModelException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityModelException" /> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public IdentityModelException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityModelException" /> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public IdentityModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
