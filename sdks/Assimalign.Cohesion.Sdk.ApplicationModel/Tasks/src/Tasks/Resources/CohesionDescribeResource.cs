using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Assimalign.Cohesion.Sdk.ApplicationModel.Tasks.Internal;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>
/// Runs a built resource executable in describe mode and writes the runtime surface it reports beside the
/// build output.
/// </summary>
/// <remarks>
/// The resource writes its frozen <c>cohesion/runtime/v1</c> surface to <c>COHESION_DESCRIBE_PATH</c> and
/// exits when its host is built, before <c>Program.cs</c> runs the host. The run receives the dependency
/// endpoints a gateway would inject, pointed at a host that never resolves, so a resource that reads its
/// references while it composes can still be described without reaching anything. A run that fails or
/// times out is a warning, never a build failure, and leaves no surface behind; a run that exits without
/// building a host has no surface.
/// </remarks>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionDescribeResource : Task
{
    // The runtime contract's frozen names (docs/RUNTIME_CONTRACT.md); this task cannot reference Core.
    private const string contractVariablePrefix = "COHESION_";
    private const string describePathVariable = "COHESION_DESCRIBE_PATH";
    private const string dependencyVariableFormat = "COHESION_DEPENDENCY_{0}_{1}_URL";
    private const string placeholderHost = "cohesion-describe.invalid";
    private const string surfaceSchema = "cohesion/runtime/v1";
    private const string warningCode = "COHSDK012";
    private const int retainedOutputLines = 200;
    private const int reportedOutputLines = 8;

    /// <summary>Gets or sets the resource project, which diagnostics name.</summary>
    [Required]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the built resource assembly, run through the .NET host when there is no apphost.</summary>
    [Required]
    public string AssemblyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the built apphost, which is run when it exists.</summary>
    public string AppHostPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the .NET host that runs the assembly when there is no apphost.</summary>
    public string DotNetHostPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the resource's own generated manifest, which lists its references.</summary>
    [Required]
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the manifests the build resolved, which give each referenced endpoint's scheme.</summary>
    public ITaskItem[] ReferencedManifests { get; set; } = [];

    /// <summary>Gets or sets the runtime surface file to write beside the build output.</summary>
    [Required]
    public string SurfacePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory the resource runs in.</summary>
    [Required]
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets how long the run may take before it is ended.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Gets whether the run produced a runtime surface.</summary>
    [Output]
    public bool Described { get; private set; }

    /// <inheritdoc />
    public override bool Execute()
    {
        string temporaryPath = Path.GetFullPath(SurfacePath) + "." + Guid.NewGuid().ToString("N") + ".describe";
        try
        {
            if (TimeoutSeconds <= 0)
            {
                Log.LogError(null, null, null, ProjectPath, 0, 0, 0, 0,
                    $"CohesionDescribeTimeoutSeconds must be a positive number of seconds; '{TimeoutSeconds}' was supplied.");
                return false;
            }

            Run(temporaryPath);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            RemoveSurface();
            Warn($"the describe run could not complete: {exception.Message}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return !Log.HasLoggedErrors;
    }

    private void Run(string temporaryPath)
    {
        ProcessStartInfo start = CreateStartInfo(temporaryPath);
        var output = new Queue<string>();
        var errors = new Queue<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => Retain(output, line.Data);
        process.ErrorDataReceived += (_, line) => Retain(errors, line.Data);
        process.Start();
        // A resource that reads the console before building its host sees end of input, not a hung build.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // A timeout past int milliseconds (about 24 days) waits as long as the platform allows instead of overflowing.
        if (!process.WaitForExit((int)Math.Min(TimeoutSeconds * 1000L, int.MaxValue)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            RemoveSurface();
            Warn($"the describe run did not finish within {TimeoutSeconds} seconds. A resource that does work before it builds its host " +
                "cannot be described; set CohesionDescribeResource=false to skip the run, or CohesionDescribeTimeoutSeconds to allow longer.");
            return;
        }

        // The parameterless wait also drains the redirected streams, so the retained output is complete.
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            RemoveSurface();
            Warn($"the describe run exited with code {process.ExitCode}, so the resource has no runtime surface. It must build its host " +
                "without a gateway: references resolve to placeholders, but a required setting needs a default and nothing may connect " +
                "before the host is built." + Describe(errors.Count > 0 ? errors : output));
            return;
        }

        if (!File.Exists(temporaryPath))
        {
            // Exiting normally without building a resource host is a resource with no control plane, not a failure.
            RemoveSurface();
            Log.LogMessage(MessageImportance.Normal, $"{ProjectPath}: the describe run exited without building a resource host, so it has no runtime surface.");
            return;
        }

        byte[] surface = File.ReadAllBytes(temporaryPath);
        if (!IsSurface(surface))
        {
            RemoveSurface();
            Warn($"the describe run wrote a document that is not a {surfaceSchema} runtime surface.");
            return;
        }

        ResourceFileWriter.WriteIfChanged(SurfacePath, surface);
        Described = true;
        Log.LogMessage(MessageImportance.Normal, $"{ProjectPath}: described its runtime surface in '{Path.GetFullPath(SurfacePath)}'.");
    }

    private ProcessStartInfo CreateStartInfo(string temporaryPath)
    {
        bool useAppHost = AppHostPath.Length != 0 && File.Exists(AppHostPath);
        var start = new ProcessStartInfo(useAppHost ? AppHostPath : DotNetHostPath.Length != 0 ? DotNetHostPath : "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = WorkingDirectory,
        };
        if (!useAppHost)
        {
            start.ArgumentList.Add(AssemblyPath);
        }

        // An inherited gateway or deployment-record variable would make the run something other than a
        // standalone describe, so every runtime-contract variable is replaced by the ones set here.
        foreach (string variable in start.Environment.Keys.Where(static key => key.StartsWith(contractVariablePrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(variable);
        }

        if (DotNetHostPath.Length != 0 && !start.Environment.ContainsKey("DOTNET_ROOT"))
        {
            start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(DotNetHostPath);
        }

        foreach (KeyValuePair<string, string> dependency in CreatePlaceholderDependencies())
        {
            start.Environment[dependency.Key] = dependency.Value;
        }

        start.Environment[describePathVariable] = temporaryPath;
        return start;
    }

    // One placeholder endpoint per referenced endpoint, in the form a gateway writes. The scheme and container
    // port come from the referenced manifest; the host is a reserved name that never resolves.
    private Dictionary<string, string> CreatePlaceholderDependencies()
    {
        var endpoints = new Dictionary<string, Dictionary<string, (string Scheme, int Port)>>(StringComparer.Ordinal);
        foreach (ITaskItem item in ReferencedManifests)
        {
            string path = item.GetMetadata("ManifestPath");
            path = path.Length != 0 ? path : item.ItemSpec;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(path));
                if (manifest.RootElement.ValueKind != JsonValueKind.Object ||
                    !manifest.RootElement.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                    !manifest.RootElement.TryGetProperty("endpoints", out JsonElement declared) || declared.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var byName = new Dictionary<string, (string Scheme, int Port)>(StringComparer.Ordinal);
                foreach (JsonElement endpoint in declared.EnumerateArray())
                {
                    if (endpoint.ValueKind == JsonValueKind.Object &&
                        endpoint.TryGetProperty("name", out JsonElement endpointName) && endpointName.ValueKind == JsonValueKind.String)
                    {
                        string scheme = endpoint.TryGetProperty("scheme", out JsonElement value) && value.ValueKind == JsonValueKind.String
                            ? value.GetString()!
                            : "tcp";
                        int port = endpoint.TryGetProperty("containerPort", out JsonElement containerPort) &&
                            containerPort.TryGetInt32(out int number) && number is > 0 and <= 65535
                            ? number
                            : 1;
                        byName[endpointName.GetString()!] = (scheme, port);
                    }
                }

                endpoints[name.GetString()!] = byName;
            }
            catch (JsonException exception)
            {
                Log.LogMessage(MessageImportance.Low, $"Skipping manifest '{path}' for describe placeholders: {exception.Message}");
            }
        }

        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
        using JsonDocument own = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));
        if (!own.RootElement.TryGetProperty("references", out JsonElement references) || references.ValueKind != JsonValueKind.Array)
        {
            return placeholders;
        }

        foreach (JsonElement reference in references.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.Object ||
                !reference.TryGetProperty("resource", out JsonElement resource) || resource.ValueKind != JsonValueKind.String ||
                !reference.TryGetProperty("endpoints", out JsonElement names) || names.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            string resourceName = resource.GetString()!;
            endpoints.TryGetValue(resourceName, out Dictionary<string, (string Scheme, int Port)>? declared);
            foreach (JsonElement endpointName in names.EnumerateArray())
            {
                if (endpointName.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(endpointName.GetString()))
                {
                    continue;
                }

                string endpoint = endpointName.GetString()!;
                (string scheme, int port) = declared is not null && declared.TryGetValue(endpoint, out (string, int) found) ? found : ("tcp", 1);
                placeholders[string.Format(CultureInfo.InvariantCulture, dependencyVariableFormat, NormalizeName(resourceName), NormalizeName(endpoint))] =
                    string.Create(CultureInfo.InvariantCulture, $"{scheme}://{placeholderHost}:{port}");
            }
        }

        return placeholders;
    }

    // The runtime contract's upper-snake rule: ASCII letters upper-cased, digits kept, anything else one underscore.
    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(character switch
            {
                >= 'a' and <= 'z' => (char)(character - ('a' - 'A')),
                >= 'A' and <= 'Z' or >= '0' and <= '9' => character,
                _ => '_',
            });
        }

        return builder.ToString();
    }

    private static bool IsSurface(byte[] document)
    {
        try
        {
            using JsonDocument surface = JsonDocument.Parse(document);
            return surface.RootElement.ValueKind == JsonValueKind.Object &&
                surface.RootElement.TryGetProperty("schema", out JsonElement schema) &&
                schema.ValueKind == JsonValueKind.String &&
                schema.GetString() == surfaceSchema;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Retain(Queue<string> lines, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (lines)
        {
            lines.Enqueue(line);
            if (lines.Count > retainedOutputLines)
            {
                lines.Dequeue();
            }
        }
    }

    // An unhandled exception's type and message lead its output; otherwise the last lines say most.
    private static string Describe(Queue<string> lines)
    {
        string[] retained;
        lock (lines)
        {
            retained = lines.ToArray();
        }

        if (retained.Length == 0)
        {
            return string.Empty;
        }

        int exception = Array.FindIndex(retained, static line => line.StartsWith("Unhandled exception.", StringComparison.Ordinal));
        IEnumerable<string> reported = exception >= 0
            ? retained.Skip(exception).Take(reportedOutputLines)
            : retained.Skip(Math.Max(0, retained.Length - reportedOutputLines));
        return Environment.NewLine + string.Join(Environment.NewLine, reported);
    }

    private void RemoveSurface()
    {
        if (File.Exists(SurfacePath))
        {
            File.Delete(SurfacePath);
        }
    }

    private void Warn(string reason) =>
        Log.LogWarning(null, warningCode, null, ProjectPath, 0, 0, 0, 0, $"{Path.GetFileNameWithoutExtension(ProjectPath)}: {reason}");
}
