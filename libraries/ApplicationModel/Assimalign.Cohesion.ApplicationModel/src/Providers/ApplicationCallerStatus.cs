namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The outcome of one <see cref="IApplicationCallerAuthenticator"/>.
/// </summary>
public enum ApplicationCallerStatus
{
    /// <summary>
    /// The authenticator does not recognize the credential; the gateway consults the next one.
    /// </summary>
    NoResult = 0,

    /// <summary>
    /// The credential is valid and <see cref="ApplicationCallerResult.Caller"/> identifies the caller.
    /// </summary>
    Authenticated,

    /// <summary>
    /// The authenticator recognized the credential but it is invalid (for example expired or
    /// badly signed); the gateway answers 401 without consulting further authenticators.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The credential is valid but the caller may not use this control plane; the gateway answers
    /// 403 without consulting further authenticators.
    /// </summary>
    Forbidden,
}
