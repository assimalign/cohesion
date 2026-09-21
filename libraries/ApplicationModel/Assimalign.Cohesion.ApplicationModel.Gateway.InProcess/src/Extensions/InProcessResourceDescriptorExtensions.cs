using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>Associates generated, composable resource entries with in-process descriptors.</summary>
public static partial class InProcessResourceDescriptorExtensions
{
    extension(IApplicationResourceDescriptor descriptor)
    {
        /// <summary>
        /// Binds an enabled project resource to its compiler-rooted executable assembly.
        /// </summary>
        /// <param name="entryAssembly">The resource assembly whose entry point is invoked.</param>
        /// <param name="contentRootPath">The resource-specific absolute content root.</param>
        /// <returns>The original descriptor, for dependency composition.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="descriptor"/>, <paramref name="entryAssembly"/>, or
        /// <paramref name="contentRootPath"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="contentRootPath"/> is empty or not absolute.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The descriptor already has a different in-process binding.
        /// </exception>
        /// <remarks>
        /// This is SDK infrastructure. Generated callers root the executable entry point with
        /// <see cref="DynamicDependencyAttribute"/>; hand-written callers must provide equivalent
        /// trimming metadata.
        /// </remarks>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [RequiresUnreferencedCode(
            "Manual in-process bindings must preserve the executable entry point. Use the Sdk.Gateway-generated resource verb.")]
        public IApplicationResourceDescriptor InProcess(
            Assembly entryAssembly,
            string contentRootPath)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(entryAssembly);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

            if (!Path.IsPathFullyQualified(contentRootPath))
            {
                throw new ArgumentException(
                    "An in-process resource content root must be an absolute path.",
                    nameof(contentRootPath));
            }

            InProcessResourceBindings.Register(
                descriptor.Resource,
                new InProcessResourceBinding(entryAssembly, Path.GetFullPath(contentRootPath)));
            return descriptor;
        }
    }
}

/// <summary>
/// Holds the in-process entry bindings of the current process. A descriptor binding is keyed by
/// the built resource instance; a manifest binding is keyed by the manifest's application and
/// resource names, so it is found whichever verb added the resource.
/// </summary>
internal static class InProcessResourceBindings
{
    private static readonly ConditionalWeakTable<IApplicationResource, InProcessResourceBinding> Bindings = new();
    private static readonly Dictionary<string, InProcessResourceBinding> ManifestBindings = new(StringComparer.Ordinal);
    private static readonly Lock ManifestLock = new();

    internal static void Register(
        IApplicationResource resource,
        InProcessResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(binding);

        if (Bindings.TryGetValue(resource, out InProcessResourceBinding? existing))
        {
            if (existing.Matches(binding))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Resource '{resource.Name}' already has a different in-process entry binding.");
        }

        Bindings.Add(resource, binding);
    }

    internal static void RegisterManifest(
        ResourceManifest manifest,
        InProcessResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(binding);

        string key = ManifestKey(manifest);
        lock (ManifestLock)
        {
            if (ManifestBindings.TryGetValue(key, out InProcessResourceBinding? existing))
            {
                if (existing.Matches(binding))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Resource '{key}' already has a different in-process entry binding.");
            }

            ManifestBindings.Add(key, binding);
        }
    }

    internal static bool TryGet(
        IApplicationResource resource,
        [NotNullWhen(true)]
        out InProcessResourceBinding? binding)
    {
        if (Bindings.TryGetValue(resource, out binding))
        {
            return true;
        }

        if (resource is IManifestResource { Manifest: { } manifest })
        {
            lock (ManifestLock)
            {
                return ManifestBindings.TryGetValue(ManifestKey(manifest), out binding);
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

internal sealed record InProcessResourceBinding(
    Assembly EntryAssembly,
    string ContentRootPath)
{
    internal bool Matches(InProcessResourceBinding other) =>
        ReferenceEquals(EntryAssembly, other.EntryAssembly)
        && string.Equals(
            ContentRootPath,
            other.ContentRootPath,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
