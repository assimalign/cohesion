using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Assimalign.Cohesion.Cli;

internal sealed class CliApplication(
    IProcessRunner processes, HttpClient http, TextReader input, TextWriter output, TextWriter error,
    string workingDirectory, string homeDirectory,
    Func<string, string?>? environment = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? timeProvider = null)
{
    // Fixed assembly metadata is preserved by NativeAOT; no runtime type discovery occurs.
    internal static string Version => StripBuildMetadata(
        typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion);

    internal static string StripBuildMetadata(string version) => version.Split('+', 2)[0];

    internal async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                output.WriteLine(Help.Text);
                output.WriteLine("Templates: " + string.Join(", ", Templates.Names));
                return 0;
            }
            if (args[0] == "--version")
            {
                output.WriteLine(Version);
                return 0;
            }

            var arguments = new Arguments(args[1..]);
            if (arguments.TakeFlag("--help") || arguments.TakeFlag("-h"))
            {
                output.WriteLine(Help.Text);
                output.WriteLine("Templates: " + string.Join(", ", Templates.Names));
                return 0;
            }
            switch (args[0])
            {
                case "new":
                    return await NewAsync(arguments, cancellationToken).ConfigureAwait(false);
                case "run":
                case "deploy":
                case "trust":
                    return await GatewayAsync(args[0], arguments, cancellationToken).ConfigureAwait(false);
                case "publish":
                    return await PublishAsync(arguments, cancellationToken).ConfigureAwait(false);
                case "parameter":
                case "status":
                    string? projectOption = arguments.TakeValue("--project");
                    string? stateOption = arguments.TakeValue("--state-root");
                    string? appOption = arguments.TakeValue("--app");
                    string project = GatewayDiscovery.ResolveProject(workingDirectory, projectOption);
                    string stateRoot = GatewayDiscovery.GetStateRoot(project, stateOption, workingDirectory);
                    string app = GatewayDiscovery.ResolveApplication(project, stateRoot, appOption);
                    var state = new LocalStateCommands(input, output, http, environment ?? Environment.GetEnvironmentVariable);
                    return args[0] == "parameter"
                        ? await state.ParameterAsync(arguments, stateRoot, app, cancellationToken).ConfigureAwait(false)
                        : await state.StatusAsync(arguments, stateRoot, app, cancellationToken).ConfigureAwait(false);
                case "login":
                    return await new DeviceLogin(http, output, error, homeDirectory, delay, timeProvider)
                        .ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
                default:
                    throw new CliException("Unknown command. Use cohesion --help.");
            }
        }
        catch (CliException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Cancelled.");
            return 130;
        }
        catch (HttpRequestException)
        {
            error.WriteLine("HTTP request failed. Check that the endpoint is available and its certificate is trusted.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("HTTP request timed out.");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or XmlException or Win32Exception or ArgumentException or FormatException)
        {
            // Serialization and OS diagnostics can contain credential values or request URLs.
            error.WriteLine("Unable to read or write the requested data, or start dotnet. Check the path, format, permissions and .NET installation.");
            return 1;
        }
    }

    private async Task<int> NewAsync(Arguments args, CancellationToken cancellationToken = default)
    {
        if (args.TakeFlag("--list"))
        {
            return await DotnetAsync(["new", "list", "cohesion", .. args.Remaining], cancellationToken).ConfigureAwait(false);
        }
        string template = args.TakeFirst() ?? throw new CliException("new requires a template name; use new --list.");
        Templates.Validate(template, args);
        int exitCode = await DotnetAsync(["new", template, .. args.Remaining], cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            error.WriteLine($"Cohesion templates are not installed. Run: dotnet new install {Templates.PackageId}::{Version}");
        }
        return exitCode;
    }

    private Task<int> PublishAsync(Arguments args, CancellationToken cancellationToken = default)
    {
        bool inContainer = args.TakeFlag("--in-container");
        string? selected = args.TakeValue("--project");
        string? positional = args.TakeFirst();
        if (selected is not null && positional is not null)
        {
            throw new CliException("publish accepts one project, either positional or --project.");
        }
        string project = positional ?? (selected is null
            ? GatewayDiscovery.ResolveProject(workingDirectory, null)
            : Path.GetFullPath(selected, workingDirectory));
        return DotnetAsync(inContainer
            ? ["publish", project, "-t:CohesionPublishImage", "-p:_CohesionImageInContainer=true", .. args.Remaining]
            : ["publish", project, .. args.Remaining], cancellationToken);
    }

    private Task<int> GatewayAsync(string verb, Arguments args, CancellationToken cancellationToken = default)
    {
        string? projectOption = args.TakeValue("--project");
        string? gateway = args.TakeValue("--gateway");
        var mapped = new List<string>();
        if (gateway is not null)
        {
            mapped.AddRange(["--gateway", gateway]);
        }
        if (verb == "deploy")
        {
            mapped.AddRange(["--mode", "apply"]);
        }
        if (verb == "trust")
        {
            string? command = args.TakeFirst();
            switch (command)
            {
                case "issue":
                    string developer = args.TakeValue("--developer")
                        ?? throw new CliException("trust issue requires --developer <name>.");
                    mapped.AddRange(["--mode", "trust-issue", "--developer", developer]);
                    break;
                case "add":
                    if (args.Has("--against"))
                    {
                        throw new CliException("trust add --against is deferred to #982; select the verifying application's gateway project instead.");
                    }
                    string peer = args.TakeFirst() ?? throw new CliException("trust add requires <peer> --from <export-url|file>.");
                    string source = args.TakeValue("--from") ?? throw new CliException("trust add requires --from <export-url|file>.");
                    mapped.AddRange(["--mode", "trust-add", "--peer", peer, "--from", source]);
                    while (args.TakeFirst(allowOptionLike: true) is string argument)
                    {
                        if (argument == "--allow")
                        {
                            string kinds = args.TakeFirst() ?? throw new CliException("--allow requires a value.");
                            mapped.AddRange(["--allow", kinds]);
                        }
                        else if (argument.StartsWith("--allow=", StringComparison.Ordinal))
                        {
                            string kinds = argument["--allow=".Length..];
                            if (string.IsNullOrWhiteSpace(kinds)) { throw new CliException("--allow requires a value."); }
                            mapped.AddRange(["--allow", kinds]);
                        }
                        else
                        {
                            mapped.Add(argument);
                        }
                    }
                    break;
                default:
                    throw new CliException("trust requires add or issue.");
            }
        }
        string project = GatewayDiscovery.ResolveProject(workingDirectory, projectOption);
        // SDK 10.0.401 retains the caller's cwd in the verified dotnet-run probe.
        // Set it explicitly so gateway state and relative arguments are project-local.
        return processes.RunAsync("dotnet", ["run", "--project", project, "--", .. mapped, .. args.Remaining],
            Path.GetDirectoryName(project)!, cancellationToken);
    }

    private Task<int> DotnetAsync(string[] arguments, CancellationToken cancellationToken = default) =>
        processes.RunAsync("dotnet", arguments, workingDirectory, cancellationToken);
}
