namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// A nonblank logical secret-seed path.
/// </summary>
/// <remarks>
/// Paths use the SecretStore seed protocol's ordinal comparison and retain their exact spelling.
/// They are logical identifiers, not file-system paths; separators and <c>certs/</c> are permitted.
/// <see langword="default"/> contains no path and is refused by the application builder.
/// </remarks>
public partial struct SecretStoreSeedPath
{
    /// <summary>
    /// Determines whether a value is a permitted logical secret-seed path.
    /// </summary>
    /// <param name="value">The candidate path.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> when the path is valid.</param>
    /// <returns><see langword="true"/> for a nonblank path.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            message = ErrorMessages.SeedPathInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
