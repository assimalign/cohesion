using System;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>Identifies a certificate bundle with one to 1024 non-control characters; whitespace alone is invalid.</summary>
public readonly partial struct ResourceCertificateKey
{
    /// <summary>Validates the bundle key.</summary>
    /// <param name="value">The candidate key.</param>
    /// <param name="message">The rejection reason.</param>
    /// <returns>Whether the key is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = !string.IsNullOrWhiteSpace(value) && value.Length <= 1024;
        foreach (char character in value ?? string.Empty) { valid &= !char.IsControl(character); }
        message = valid ? null : ErrorMessages.CertificateReferenceInvalid;
        return valid;
    }
}
