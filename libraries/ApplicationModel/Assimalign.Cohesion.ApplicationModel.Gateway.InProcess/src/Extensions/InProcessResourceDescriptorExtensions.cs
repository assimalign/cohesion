using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

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
