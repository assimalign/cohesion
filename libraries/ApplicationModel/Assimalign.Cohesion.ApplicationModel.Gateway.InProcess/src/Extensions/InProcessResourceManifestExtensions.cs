using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>Associates generated, composable resource manifests with in-process bindings.</summary>
/// <remarks>
/// A manifest binding is keyed by the manifest's application and resource names, so a resource
/// added through any verb that accepts the generated <c>Manifests.&lt;Name&gt;</c> member — the
/// generated <c>Add&lt;Name&gt;()</c> verb, the area's own <c>Add&lt;Area&gt;(manifest)</c>, or a
/// third-party application model's verb over it — is colocatable by the in-process gateway.
/// </remarks>
public static class InProcessResourceManifestExtensions
{
    extension(ResourceManifest manifest)
    {
        /// <summary>
        /// Binds an enabled project resource manifest to its compiler-rooted executable assembly.
        /// </summary>
        /// <param name="entryAssembly">The resource assembly whose entry point is invoked.</param>
        /// <param name="contentRootPath">The resource-specific absolute content root.</param>
        /// <returns>The original manifest, for chaining.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="manifest"/>, <paramref name="entryAssembly"/>, or
        /// <paramref name="contentRootPath"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="contentRootPath"/> is empty or not absolute, or the manifest has no
        /// application or resource name.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The same application and resource names already have a different in-process binding.
        /// </exception>
        /// <remarks>
        /// This is SDK infrastructure. The generated <c>Gateway.CreateBuilder(args)</c> registers
        /// every enabled, composable project resource of the gateway build and roots each
        /// executable entry point with <see cref="DynamicDependencyAttribute"/>; hand-written
        /// callers must provide equivalent trimming metadata.
        /// </remarks>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [RequiresUnreferencedCode(
            "Manual in-process bindings must preserve the executable entry point. Use the Sdk.Gateway-generated Gateway.CreateBuilder.")]
        public ResourceManifest InProcess(
            Assembly entryAssembly,
            string contentRootPath)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(entryAssembly);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

            if (!Path.IsPathFullyQualified(contentRootPath))
            {
                throw new ArgumentException(
                    "An in-process resource content root must be an absolute path.",
                    nameof(contentRootPath));
            }

            InProcessResourceBindings.RegisterManifest(
                manifest,
                new InProcessResourceBinding(entryAssembly, Path.GetFullPath(contentRootPath)));
            return manifest;
        }
    }
}
