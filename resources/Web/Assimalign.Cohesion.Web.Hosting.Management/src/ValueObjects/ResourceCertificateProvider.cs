using System;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>Names an explicitly configured certificate provider using one to 128 ASCII letters, digits, dots, dashes or underscores.</summary>
public readonly partial struct ResourceCertificateProvider
{
    /// <summary>Validates the provider identifier.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="message">The rejection reason.</param>
    /// <returns>Whether the identifier is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = !string.IsNullOrEmpty(value) && value.Length <= 128;
        foreach (char character in value ?? string.Empty) { valid &= char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'; }
        message = valid ? null : ErrorMessages.CertificateReferenceInvalid;
        return valid;
    }
}
