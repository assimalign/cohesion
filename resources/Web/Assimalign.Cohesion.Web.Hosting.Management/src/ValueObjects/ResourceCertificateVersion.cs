using System;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>Identifies an immutable certificate version; blank values, controls, wildcards and latest aliases are refused.</summary>
public readonly partial struct ResourceCertificateVersion
{
    /// <summary>Validates the immutable version identifier.</summary>
    /// <param name="value">The candidate version.</param>
    /// <param name="message">The rejection reason.</param>
    /// <returns>Whether the version is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value != "*" && !string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase);
        foreach (char character in value ?? string.Empty) { valid &= !char.IsControl(character); }
        message = valid ? null : ErrorMessages.CertificateReferenceInvalid;
        return valid;
    }
}
