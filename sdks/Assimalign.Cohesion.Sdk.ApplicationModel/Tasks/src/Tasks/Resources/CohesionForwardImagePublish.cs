using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>Invokes a fresh RID-specific publish process or forwards through the CLI's private container channel.</summary>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionForwardImagePublish : Task
{
    /// <summary>Gets or sets the consumer project.</summary>
    [Required]
    public string ProjectPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the complete property vector to preserve across the CLI boundary.</summary>
    public ITaskItem[] Properties { get; set; } = [];
    /// <summary>Gets or sets whether to use the in-container CLI route instead of a host dotnet publish.</summary>
    public bool InContainer { get; set; }
    /// <summary>Gets or sets the configuration for the Release-only unavailable-route diagnostic.</summary>
    public string Configuration { get; set; } = string.Empty;
    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            var start = new ProcessStartInfo(InContainer ? "cohesion" : "dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ProjectPath)!,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            string[] arguments = InContainer
                ? ["publish", "--in-container", "--project", ProjectPath]
                : ["publish", ProjectPath, "-t:_CohesionPublishImagePayload", "--nologo"];
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (BuildEngine is IBuildEngine6 engine)
            {
                foreach (KeyValuePair<string, string> property in engine.GetGlobalProperties())
                {
                    properties[property.Key] = property.Value;
                }
            }
            foreach (ITaskItem property in Properties)
            {
                int separator = property.ItemSpec.IndexOf('=');
                properties[property.ItemSpec[..separator]] = property.ItemSpec[(separator + 1)..];
            }
            foreach (KeyValuePair<string, string> property in properties)
            {
                // ArgumentList avoids the shell; MSBuild still requires escaping its own list delimiters.
                string value = property.Value.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal);
                start.ArgumentList.Add("-p:" + property.Key + "=" + value);
            }

            using Process process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Log.LogMessage(MessageImportance.High, stdout.GetAwaiter().GetResult());
            string errors = stderr.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(errors))
            {
                Log.LogMessage(MessageImportance.High, errors);
            }
            if (process.ExitCode == 0)
            {
                return true;
            }

            Log.LogError(null, InContainer && Configuration == "Release" ? "COHSDK003" : null, null, ProjectPath, 0, 0, 0, 0, $"Image publish process failed with exit code {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            Log.LogError(null, InContainer && Configuration == "Release" ? "COHSDK003" : null, null, ProjectPath, 0, 0, 0, 0, $"Cannot start image publish process: {exception.Message}");
        }
        return false;
    }
}
