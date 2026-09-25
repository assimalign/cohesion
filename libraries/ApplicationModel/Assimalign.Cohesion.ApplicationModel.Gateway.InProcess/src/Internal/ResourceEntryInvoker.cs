using System;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

internal interface IResourceEntryInvoker
{
    IResourceEntryInvocation Invoke(
        InProcessResourceArtifact artifact,
        ResourceContext context);
}

internal sealed class ResourceEntryInvoker : IResourceEntryInvoker
{
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Sdk.Gateway emits a DynamicDependency root for every generated in-process resource binding.")]
    public IResourceEntryInvocation Invoke(
        InProcessResourceArtifact artifact,
        ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(context);

        using (ResourceRuntime.CreateScope(context))
        {
            return ResourceRuntime.IsEntryRegistered(artifact.EntryAssembly)
                ? ResourceRuntime.InvokeEntry(
                    artifact.EntryAssembly,
                    Array.Empty<string>())
                : ResourceRuntime.InvokeEntryPoint(
                    artifact.EntryAssembly,
                    Array.Empty<string>());
        }
    }
}
