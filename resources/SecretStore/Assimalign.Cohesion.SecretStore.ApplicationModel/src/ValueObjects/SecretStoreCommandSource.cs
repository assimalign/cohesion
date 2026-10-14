using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>A secret command's parameter or resource source expression.</summary>
/// <remarks>Source spelling is ordinal; only the exact <c>parameter:</c> prefix selects a parameter.</remarks>
public partial struct SecretStoreCommandSource
{
    /// <summary>Checks the source grammar without resolving or exposing its secret.</summary>
    /// <param name="value">The source expression.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> on success.</param>
    /// <returns>Whether the expression names a nonblank parameter or a resource and nonblank key.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = SecretStoreCommandGrammar.IsValidSource(value);
        message = valid ? null : ErrorMessages.CommandSourceInvalid;
        return valid;
    }
}
