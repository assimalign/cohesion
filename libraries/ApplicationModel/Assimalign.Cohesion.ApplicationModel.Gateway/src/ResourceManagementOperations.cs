using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Names the management operations a trusted issuer's <see cref="TrustedIssuer.AllowedManagementOperations"/> grants,
/// and the operations a management event reports.
/// </summary>
/// <remarks>
/// <para>
/// A grant is an operation name, which covers every target, or <c>&lt;operation&gt;:&lt;target&gt;</c>, which covers
/// one: a section path (<c>configuration:write:Web:Cors</c>), an action name (<c>actions:invoke:flush-cache</c>), or a
/// platform surface (<c>platform:write:kubernetes/hpa</c>). <see cref="All"/> grants everything. An empty grant list
/// grants nothing, unlike command kinds, where an empty list permits every kind.
/// </para>
/// <para>
/// The application's own trust key grants <see cref="All"/>: whoever holds it can already mint any credential for the
/// application, so the developer credentials it issues manage the application's resources.
/// </para>
/// </remarks>
public static class ResourceManagementOperations
{
    /// <summary>Grants every management operation.</summary>
    public const string All = "*";

    /// <summary>Reads a resource's configuration, its convergence status and its instance inventory.</summary>
    public const string ReadConfiguration = "configuration:read";

    /// <summary>Changes a section's management override; a target names one section.</summary>
    public const string WriteConfiguration = "configuration:write";

    /// <summary>Replaces the caller's own application's claims; a target names one section.</summary>
    public const string WriteClaims = "claims:write";

    /// <summary>Runs an action; a target names one action.</summary>
    public const string InvokeActions = "actions:invoke";

    /// <summary>Reads a platform-published surface.</summary>
    public const string ReadPlatform = "platform:read";

    /// <summary>Changes a platform-published surface; a target names one, as <c>&lt;platform&gt;/&lt;extension&gt;</c>.</summary>
    public const string WritePlatform = "platform:write";

    /// <summary>
    /// Reported by a management event, never granted: a deployment started a new generation and discarded the
    /// overrides of the previous one.
    /// </summary>
    public const string DiscardOverrides = "configuration:discard";

    /// <summary>
    /// Reported by a management event, never granted: the gateway stored a management change but the platform did not
    /// take it, so the instances or the platform's objects receive it only at their next start or reconcile.
    /// </summary>
    public const string DeliverRecord = "record:deliver";

    /// <summary>Decides whether a caller's grants allow an operation.</summary>
    /// <param name="grants">The caller's management grants.</param>
    /// <param name="operation">The operation, one of this class's names.</param>
    /// <param name="target">The section, action or surface the operation acts on, or null for one with no target.</param>
    /// <returns>
    /// Whether the grants hold <see cref="All"/>, the operation itself, or the operation qualified by
    /// <paramref name="target"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="grants"/> or <paramref name="operation"/> is null.</exception>
    public static bool IsGranted(IReadOnlyList<string> grants, string operation, string? target = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(operation);
        for (int index = 0; index < grants.Count; index++)
        {
            string grant = grants[index];
            if (string.Equals(grant, All, StringComparison.Ordinal) || string.Equals(grant, operation, StringComparison.Ordinal))
            {
                return true;
            }

            if (target is not null &&
                grant.Length == operation.Length + 1 + target.Length &&
                grant.StartsWith(operation, StringComparison.Ordinal) &&
                grant[operation.Length] == ':' &&
                grant.EndsWith(target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
