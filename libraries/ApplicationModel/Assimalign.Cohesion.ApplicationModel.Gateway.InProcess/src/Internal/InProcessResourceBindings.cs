using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

/// <summary>
/// Holds the in-process entry bindings of the current process. A descriptor binding is keyed by
/// the built resource instance; a manifest binding is keyed by the manifest's application and
/// resource names, so it is found whichever verb added the resource.
/// </summary>
internal static class InProcessResourceBindings
{
    private static readonly ConditionalWeakTable<IApplicationResource, InProcessResourceBinding> _bindings = new();
    private static readonly Dictionary<string, InProcessResourceBinding> _manifestBindings = new(StringComparer.Ordinal);
    private static readonly Lock _manifestLock = new();

    internal static void Register(
        IApplicationResource resource,
        InProcessResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(binding);

        if (_bindings.TryGetValue(resource, out InProcessResourceBinding? existing))
        {
            if (existing.Matches(binding))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Resource '{resource.Name}' already has a different in-process entry binding.");
        }

        _bindings.Add(resource, binding);
    }

    internal static void RegisterManifest(
        ResourceManifest manifest,
        InProcessResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(binding);

        string key = ManifestKey(manifest);
        lock (_manifestLock)
        {
            if (_manifestBindings.TryGetValue(key, out InProcessResourceBinding? existing))
            {
                if (existing.Matches(binding))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Resource '{key}' already has a different in-process entry binding.");
            }

            _manifestBindings.Add(key, binding);
        }
    }

    internal static bool TryGet(
        IApplicationResource resource,
        [NotNullWhen(true)]
        out InProcessResourceBinding? binding)
    {
        if (_bindings.TryGetValue(resource, out binding))
        {
            return true;
        }

        if (resource is IManifestResource { Manifest: { } manifest })
        {
            lock (_manifestLock)
            {
                return _manifestBindings.TryGetValue(ManifestKey(manifest), out binding);
            }
        }

        binding = null;
        return false;
    }

    private static string ManifestKey(ResourceManifest manifest)
    {
        string application = manifest.Application.ToString();
        string name = manifest.Name.ToString();
        if (string.IsNullOrWhiteSpace(application) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "An in-process manifest binding requires the manifest's application and resource names.",
                nameof(manifest));
        }

        return application + "/" + name;
    }
}
