namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// How an app registration exercises a permission it holds on an API. The member values are
/// stable and additive-only.
/// </summary>
public enum IdentityClientPermissionKind
{
    /// <summary>
    /// No kind. This is the kind of a <see langword="default"/> permission, which every
    /// registration boundary refuses.
    /// </summary>
    None = 0,

    /// <summary>
    /// The permission is a scope of the API, exercised on behalf of a signed-in user.
    /// </summary>
    Delegated = 1,

    /// <summary>
    /// The permission is a role of the API that applications may hold, exercised by the
    /// application as itself.
    /// </summary>
    Application = 2,
}
