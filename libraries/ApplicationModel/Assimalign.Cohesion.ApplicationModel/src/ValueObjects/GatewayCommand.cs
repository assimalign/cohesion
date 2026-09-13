using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Carries the validated arguments for a gateway-owned one-shot command mode.
/// </summary>
public sealed class GatewayCommand
{
    /// <summary>Initializes a gateway command.</summary>
    /// <param name="mode">The one-shot command mode.</param>
    /// <param name="developerName">The developer name for <see cref="GatewayRunMode.TrustIssue"/>.</param>
    /// <param name="peerName">The peer name for <see cref="GatewayRunMode.TrustAdd"/>.</param>
    /// <param name="exportSource">The peer export path or URI for <see cref="GatewayRunMode.TrustAdd"/>.</param>
    /// <param name="allowedCommandKinds">Allowed wire kinds for trust-add; null or empty permits every kind.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mode"/> is not a gateway command mode.
    /// </exception>
    /// <exception cref="ArgumentException">The arguments do not match <paramref name="mode"/>.</exception>
    public GatewayCommand(
        GatewayRunMode mode,
        string? developerName = null,
        string? peerName = null,
        string? exportSource = null,
        IReadOnlyList<string>? allowedCommandKinds = null)
    {
        if (mode is not GatewayRunMode.TrustIssue and not GatewayRunMode.TrustAdd)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode is not a gateway command.");
        }

        if (mode == GatewayRunMode.TrustIssue)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(developerName);
            if (peerName is not null || exportSource is not null || allowedCommandKinds is not null)
            {
                throw new ArgumentException("TrustIssue accepts only a developer name.", nameof(mode));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            ArgumentException.ThrowIfNullOrWhiteSpace(exportSource);
            if (developerName is not null)
            {
                throw new ArgumentException("TrustAdd accepts only a peer name and export source.", nameof(mode));
            }
        }

        Mode = mode;
        DeveloperName = developerName;
        PeerName = peerName;
        ExportSource = exportSource;
        AllowedCommandKinds = Array.AsReadOnly((allowedCommandKinds ?? []).Select(static kind =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            return kind;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the command run mode.</summary>
    public GatewayRunMode Mode { get; }

    /// <summary>Gets the developer name for a trust-issue command.</summary>
    public string? DeveloperName { get; }

    /// <summary>Gets the peer application name for a trust-add command.</summary>
    public string? PeerName { get; }

    /// <summary>Gets the peer export file path or control-plane URI for a trust-add command.</summary>
    public string? ExportSource { get; }

    /// <summary>Gets allowed command kinds for the trust grant; an absent or empty list permits every kind.</summary>
    public IReadOnlyList<string> AllowedCommandKinds { get; }
}
