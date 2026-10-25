namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The outcome of a management API operation. Each refusal carries a wire error code, given in its summary, that the
/// response document names as <c>error</c>.
/// </summary>
public enum ResourceManagementStatus
{
    /// <summary>The operation succeeded (HTTP 200).</summary>
    Ok,

    /// <summary>A configuration patch or claim set does not conform to the section's schema (HTTP 400, <c>invalid-configuration</c>).</summary>
    InvalidConfiguration,

    /// <summary>An action input does not conform to the action's schema (HTTP 400, <c>invalid-input</c>).</summary>
    InvalidInput,

    /// <summary>The gateway realizes no such resource (HTTP 404, <c>unknown-resource</c>).</summary>
    UnknownResource,

    /// <summary>The resource's runtime surface declares no such section (HTTP 404, <c>unknown-section</c>).</summary>
    UnknownSection,

    /// <summary>The resource's runtime surface declares no such action (HTTP 404, <c>unknown-action</c>).</summary>
    UnknownAction,

    /// <summary>The platform publishes no such surface for the resource (HTTP 404, <c>unknown-platform-surface</c>).</summary>
    UnknownPlatformSurface,

    /// <summary>Only a deployment sets the section (HTTP 409, <c>deployment-only</c>).</summary>
    DeploymentOnly,

    /// <summary>The section shapes shared backend data, which code, claims and actions change (HTTP 409, <c>not-overridable</c>).</summary>
    NotOverridable,

    /// <summary>The section does not accept claims (HTTP 409, <c>not-claimable</c>).</summary>
    NotClaimable,

    /// <summary>A claimed entry is already owned by another application or set by the resource's own deployment (HTTP 409, <c>claim-conflict</c>).</summary>
    ClaimConflict,

    /// <summary>The record changed since the version the caller named in <c>If-Match</c> (HTTP 412, <c>version-moved</c>).</summary>
    VersionMoved,

    /// <summary>This gateway keeps no deployment records (HTTP 501, <c>records-unsupported</c>).</summary>
    RecordsUnsupported,

    /// <summary>
    /// The operation needs something the platform cannot provide yet: a deployed record, or an instance that answers
    /// (HTTP 503, <c>unavailable</c>).
    /// </summary>
    Unavailable,
}
