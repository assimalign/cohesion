using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>A nonblank logical write path outside the reserved trust and certificate paths.</summary>
/// <remarks>Paths use the store protocol's ordinal, case-sensitive identity and reserved prefixes.</remarks>
public partial struct SecretStoreCommandPath
{
    /// <summary>Checks whether an add-secret command may write the path.</summary>
    /// <param name="value">The logical path.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> on success.</param>
    /// <returns>Whether the path is nonblank and outside the reserved namespaces.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = SecretStoreCommandGrammar.IsValidPath(value);
        message = valid ? null : ErrorMessages.CommandPathInvalid;
        return valid;
    }
}
