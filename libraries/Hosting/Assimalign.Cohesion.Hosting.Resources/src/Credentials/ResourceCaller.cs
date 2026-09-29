using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Describes the authenticated caller a credential verifier mapped a presented credential to.
/// </summary>
/// <param name="Application">
/// The application the caller acts for. For the default application-key credential this is the token's
/// <c>iss</c>: the resource's own application for its gateway, or a peer application's trusted issuer.
/// Resources authorize ownership and same-application access by comparing this value.
/// </param>
/// <param name="Subject">
/// The calling identity within <paramref name="Application"/>: the gateway name for a gateway credential,
/// or the emitting resource for a telemetry credential.
/// </param>
/// <param name="Kind">The caller classification.</param>
/// <param name="AllowedCommandKinds">
/// The command kinds the caller may send; empty permits every kind the resource accepts.
/// </param>
public sealed record ResourceCaller(
    string Application,
    string Subject,
    ResourceCallerKind Kind,
    IReadOnlyList<string> AllowedCommandKinds);
