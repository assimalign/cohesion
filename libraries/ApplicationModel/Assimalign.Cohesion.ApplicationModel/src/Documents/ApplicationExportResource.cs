using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one resource published by an application export.
/// </summary>
public sealed class ApplicationExportResource
{
    /// <summary>
    /// Initializes an exported resource.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="kind">The resource-area kind.</param>
    /// <param name="manifestHash">The canonical manifest hash.</param>
    /// <param name="endpoints">The resource's observed endpoints.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="kind"/>, <paramref name="manifestHash"/>,
    /// or <paramref name="endpoints"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ApplicationExportResource(
        string name,
        string kind,
        string manifestHash,
        IReadOnlyList<ApplicationExportEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(manifestHash);
        ArgumentNullException.ThrowIfNull(endpoints);

        var endpointCopy = new ApplicationExportEndpoint[endpoints.Count];
        for (int index = 0; index < endpointCopy.Length; index++)
        {
            endpointCopy[index] = endpoints[index];
        }

        Name = name;
        Kind = kind;
        ManifestHash = manifestHash;
        Endpoints = new ReadOnlyCollection<ApplicationExportEndpoint>(endpointCopy);
    }

    /// <summary>Gets the resource name.</summary>
    public string Name { get; }

    /// <summary>Gets the resource-area kind.</summary>
    public string Kind { get; }

    /// <summary>Gets the canonical manifest hash.</summary>
    public string ManifestHash { get; }

    /// <summary>Gets the resource's observed endpoints.</summary>
    public IReadOnlyList<ApplicationExportEndpoint> Endpoints { get; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("Application-export resource name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new InvalidDataException(
                $"Application-export resource '{Name}' kind must not be empty.");
        }

        if (!IsSha256(ManifestHash))
        {
            throw new InvalidDataException(
                $"Application-export resource '{Name}' manifestHash must be a 64-character hexadecimal SHA-256 hash.");
        }

        var endpointNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Endpoints.Count; index++)
        {
            ApplicationExportEndpoint endpoint = Endpoints[index]
                ?? throw new InvalidDataException(
                    $"Application-export resource '{Name}' endpoints must not contain null entries.");

            if (string.IsNullOrWhiteSpace(endpoint.Name))
            {
                throw new InvalidDataException(
                    $"Application-export resource '{Name}' endpoint names must not be empty.");
            }

            if (!endpointNames.Add(endpoint.Name))
            {
                throw new InvalidDataException(
                    $"Application-export resource '{Name}' contains duplicate endpoint '{endpoint.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(endpoint.Internal))
            {
                throw new InvalidDataException(
                    $"Application-export resource '{Name}' endpoint '{endpoint.Name}' internal address must not be empty.");
            }
        }
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool isHex = character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
