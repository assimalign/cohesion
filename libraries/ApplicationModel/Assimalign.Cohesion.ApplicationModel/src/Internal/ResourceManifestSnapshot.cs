using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal static class ResourceManifestSnapshot
{
    public static ResourceManifest Create(ResourceManifest source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source with
        {
            Artifact = source.Artifact is null ? null! : source.Artifact with { },
            Endpoints = Copy(source.Endpoints, static endpoint =>
                endpoint is null ? null! : endpoint with { }),
            Probes = Copy(source.Probes),
            ControlPlane = source.ControlPlane is null ? null! : source.ControlPlane with { },
            Mounts = Copy(source.Mounts, static mount =>
                mount is null ? null! : mount with { }),
            Settings = Copy(source.Settings, static setting =>
                setting is null ? null! : setting with { }),
            References = Copy(source.References, static reference =>
                reference is null
                    ? null!
                    : reference with { Endpoints = Copy(reference.Endpoints, static endpoint => endpoint) }),
            Commands = Copy(source.Commands, static command =>
                command is null ? null! : command with { }),
            EnvironmentVariables = Copy(source.EnvironmentVariables),
            Lifecycle = source.Lifecycle is null ? null! : source.Lifecycle with { },
            Properties = Copy(source.Properties),
        };
    }

    private static ResourceManifestProbes Copy(ResourceManifestProbes? probes)
    {
        if (probes is null)
        {
            return null!;
        }

        return probes with
        {
            Readiness = Copy(probes.Readiness),
            Liveness = Copy(probes.Liveness),
            Startup = Copy(probes.Startup),
        };
    }

    private static ResourceManifestProbe? Copy(ResourceManifestProbe? probe)
    {
        if (probe is null)
        {
            return null;
        }

        return probe with
        {
            Exec = probe.Exec is null
                ? null
                : Copy(probe.Exec, static argument => argument),
        };
    }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? source, Func<T, T> clone)
    {
        if (source is null)
        {
            return null!;
        }

        var copy = new T[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = clone(source[index]);
        }

        return new ReadOnlyCollection<T>(copy);
    }

    private static IReadOnlyDictionary<string, string> Copy(
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return null!;
        }

        var copy = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach ((string key, string value) in source)
        {
            copy.Add(key, value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
