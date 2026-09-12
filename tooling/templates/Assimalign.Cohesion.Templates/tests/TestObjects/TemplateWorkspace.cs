using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Assimalign.Cohesion.Templates.Tests;

internal sealed class TemplateWorkspace : IDisposable
{
    private bool _installed;
    internal string Root { get; } = Path.Combine(TemplateRepository.Root, "_out", "verify-39", "w", Guid.NewGuid().ToString("N")[..8]);

    internal TemplateWorkspace()
    {
        Directory.CreateDirectory(Root);
        // Consumers stay inside the permitted repository, but must not inherit its build targets.
        File.WriteAllText(Path.Combine(Root, "Directory.Build.targets"), "<Project />");
    }

    internal async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        string packageDirectory = Path.Combine(Root, "pack");
        await RunAsync(TemplateRepository.Root,
            ["pack", Path.Combine(TemplateRepository.ProjectRoot, "src", "Assimalign.Cohesion.Templates.csproj"), "-c", "Release", "-o", packageDirectory, "--nologo"], cancellationToken);
        string package = Directory.GetFiles(packageDirectory, "*.nupkg").Single();
        await RunAsync(Root, ["new", "install", package], cancellationToken);
        _installed = true;
    }

    internal async Task<string> InstantiateAsync(string template, string name, string? topology = null,
        string? applicationName = null, CancellationToken cancellationToken = default)
    {
        string output = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        var arguments = new List<string> { "new", template, "-n", name, "-o", output };
        if (topology is not null)
        {
            arguments.AddRange(["--topology", topology]);
        }

        if (applicationName is not null)
        {
            arguments.AddRange(["--applicationName", applicationName]);
        }

        await RunAsync(Root, arguments, cancellationToken);
        return output;
    }

    internal async Task BuildAsync(string output, CancellationToken cancellationToken = default)
    {
        string globalPath = Path.Combine(output, "global.json");
        JsonObject global = JsonNode.Parse(File.ReadAllText(globalPath))!.AsObject();
        JsonObject sdks = global["msbuild-sdks"]!.AsObject();
        foreach (string id in sdks.Select(entry => entry.Key).ToArray())
        {
            sdks[id] = JsonValue.Create(TemplateRepository.FeedVersion);
        }

        File.WriteAllText(globalPath, global.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "cohesion-smoke"), new XAttribute("value", TemplateRepository.Feed)),
                new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
            new XElement("packageSourceMapping",
                new XElement("packageSource", new XAttribute("key", "cohesion-smoke"),
                    new XElement("package", new XAttribute("pattern", "Assimalign.Cohesion.*"))),
                new XElement("packageSource", new XAttribute("key", "nuget.org"),
                    new XElement("package", new XAttribute("pattern", "*"))))))
            .Save(Path.Combine(output, "nuget.config"));
        await RunAsync(output, ["build", "--nologo", "-m:1", "-nr:false"], cancellationToken);
    }

    internal async Task<string> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(Root, "cli");
        start.Environment["NUGET_PACKAGES"] = Path.Combine(Root, "nuget");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        string result = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        File.AppendAllText(Path.Combine(Root, "dotnet.log"), $"dotnet {string.Join(' ', arguments)}\n{result}\n");
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} exited {process.ExitCode} in {workingDirectory}:\n{result}");
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            if (_installed)
            {
                RunAsync(Root, ["new", "uninstall", "Assimalign.Cohesion.Templates"]).GetAwaiter().GetResult();
            }
        }
        finally
        {
            // Keep logs and emitted projects reviewable; isolated package extracts are disposable.
            foreach (string name in new[] { "cli", "nuget" })
            {
                string directory = Path.GetFullPath(Path.Combine(Root, name));
                if (!directory.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Workspace cleanup escaped its owned directory.");
                }

                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // A build server may still hold an extract; the workspace is isolated and retained.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the original test result if the OS delays releasing package files.
                }
            }
        }
    }
}
