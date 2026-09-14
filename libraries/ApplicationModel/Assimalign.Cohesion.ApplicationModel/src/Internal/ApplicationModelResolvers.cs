using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class ExecutableApplicationModelResolver : IApplicationModelResolver
{
    private readonly string _path;

    public ExecutableApplicationModelResolver(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationModelDocument document = await DescribeAsync(
            context,
            Array.Empty<ResourceName>(),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ResourceName> matchingRealizations =
            ApplicationModelResolverValidation.FindUnrealizedExternals(
                document,
                context.Realize);
        if (matchingRealizations.Count != 0)
        {
            document = await DescribeAsync(
                context,
                matchingRealizations,
                cancellationToken).ConfigureAwait(false);
        }

        return document.ToModel(context.RunMode, context.GatewayIdentity);
    }

    private async Task<ApplicationModelDocument> DescribeAsync(
        ApplicationModelResolutionContext context,
        IReadOnlyList<ResourceName> realize,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.Equals(Path.GetExtension(_path), ".dll", StringComparison.OrdinalIgnoreCase)
                ? "dotnet"
                : _path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (string.Equals(Path.GetExtension(_path), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(_path);
        }

        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("describe");
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add(context.Environment.Name.ToString());
        for (int index = 0; index < realize.Count; index++)
        {
            startInfo.ArgumentList.Add("--realize");
            startInfo.ArgumentList.Add(realize[index].ToString());
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start gateway executable '{_path}'.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateAndDrainAsync(process, outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Gateway executable '{_path}' failed in --mode describe with exit code " +
                $"{process.ExitCode}: {error.Trim()}");
        }

        return ApplicationModelDocument.Parse(output);
    }

    private static async Task TerminateAndDrainAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and termination.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Cancellation must still release the redirected readers if termination races
            // operating-system process cleanup.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process handle was already released after exit.
        }

        try
        {
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Closing redirected readers is the final cancellation fallback when an exited
            // process left inherited pipe handles in a descendant.
        }
        catch (ObjectDisposedException)
        {
            // The redirected readers were closed as the canceled read unwound.
        }
        catch (InvalidOperationException)
        {
            // The redirected readers were closed as the canceled read unwound.
        }
        catch (OperationCanceledException)
        {
            // The redirected reads use the caller's token and are expected to cancel here.
        }
    }
}

internal sealed class FileApplicationModelResolver : IApplicationModelResolver
{
    private readonly string _path;

    public FileApplicationModelResolver(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public async ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ApplicationExportDocument export = await ApplicationExportDocument.LoadAsync(
            _path,
            cancellationToken).ConfigureAwait(false);
        ApplicationModelResolverValidation.ValidateExportRealizations(
            export.Model,
            context.Realize);
        return export.Model.ToModel(context.RunMode, context.GatewayIdentity);
    }
}

internal sealed class GatewayApplicationModelResolver : IApplicationModelResolver
{
    private readonly Uri _address;
    private readonly IControlPlaneClient _client;

    public GatewayApplicationModelResolver(Uri address, IControlPlaneClient client)
    {
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ApplicationExportDocument export = await _client.GetApplicationAsync(
            _address,
            cancellationToken).ConfigureAwait(false);
        ApplicationModelResolverValidation.ValidateExportRealizations(
            export.Model,
            context.Realize);
        return export.Model.ToModel(context.RunMode, context.GatewayIdentity);
    }
}

internal static class ApplicationModelResolverValidation
{
    public static IReadOnlyList<ResourceName> FindUnrealizedExternals(
        ApplicationModelDocument model,
        IReadOnlyList<ResourceName> requested)
    {
        var matches = new List<ResourceName>();
        for (int requestIndex = 0; requestIndex < requested.Count; requestIndex++)
        {
            for (int resourceIndex = 0; resourceIndex < model.Resources.Count; resourceIndex++)
            {
                ApplicationModelResourceDocument resource = model.Resources[resourceIndex];
                if (resource.External is ApplicationModelExternalDocument external
                    && !external.IsRealized
                    && string.Equals(
                        resource.Name,
                        requested[requestIndex].ToString(),
                        StringComparison.Ordinal))
                {
                    matches.Add(requested[requestIndex]);
                    break;
                }
            }
        }

        return matches;
    }

    public static void ValidateExportRealizations(
        ApplicationModelDocument model,
        IReadOnlyList<ResourceName> requested)
    {
        for (int requestIndex = 0; requestIndex < requested.Count; requestIndex++)
        {
            ApplicationModelExternalDocument? external = null;
            for (int resourceIndex = 0; resourceIndex < model.Resources.Count; resourceIndex++)
            {
                ApplicationModelResourceDocument resource = model.Resources[resourceIndex];
                if (string.Equals(
                        resource.Name,
                        requested[requestIndex].ToString(),
                        StringComparison.Ordinal))
                {
                    external = resource.External;
                    break;
                }
            }

            if (external is not null && !external.IsRealized)
            {
                throw new InvalidOperationException(
                    $"--realize names external '{requested[requestIndex]}', but the imported export " +
                    "does not contain that resource as already realized. Use the referenced gateway " +
                    "executable in Local so it can describe the realized closure.");
            }
        }
    }
}

internal sealed class EnvironmentApplicationModelResolver : IApplicationModelResolver
{
    private readonly IApplicationModelResolver _local;
    private readonly IApplicationModelResolver _deployed;

    public EnvironmentApplicationModelResolver(
        IApplicationModelResolver local,
        IApplicationModelResolver deployed)
    {
        _local = local;
        _deployed = deployed;
    }

    public ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.Environment.IsLocal ? _local : _deployed).ResolveAsync(
            context,
            cancellationToken);
    }
}
