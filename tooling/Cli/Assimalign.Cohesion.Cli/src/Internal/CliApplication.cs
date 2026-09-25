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

namespace Assimalign.Cohesion.Cli.Internal;

internal sealed class CliApplication
{
    private readonly IProcessRunner _processes;
    private readonly HttpClient _http;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _workingDirectory;
    private readonly string _homeDirectory;
    private readonly Func<string, string?>? _environment;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly TimeProvider? _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="CliApplication"/> class.</summary>
    /// <param name="processes">The runner that starts child processes such as dotnet.</param>
    /// <param name="http">The HTTP client used for control-plane and IdentityHub requests.</param>
    /// <param name="input">The reader for standard input.</param>
    /// <param name="output">The writer for standard output.</param>
    /// <param name="error">The writer for standard error.</param>
    /// <param name="workingDirectory">The directory that relative paths and project discovery resolve against.</param>
    /// <param name="homeDirectory">The user's home directory, where login credentials are stored.</param>
    /// <param name="environment">The optional environment variable reader; defaults to the process environment.</param>
    /// <param name="delay">The optional delay function used while polling; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="timeProvider">The optional time source; defaults to <see cref="TimeProvider.System"/>.</param>
    public CliApplication(
        IProcessRunner processes, HttpClient http, TextReader input, TextWriter output, TextWriter error,
        string workingDirectory, string homeDirectory,
        Func<string, string?>? environment = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null)
    {
        _processes = processes;
        _http = http;
        _input = input;
        _output = output;
        _error = error;
        _workingDirectory = workingDirectory;
        _homeDirectory = homeDirectory;
        _environment = environment;
        _delay = delay;
        _timeProvider = timeProvider;
    }

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
                _output.WriteLine(Help.Text);
                _output.WriteLine("Templates: " + string.Join(", ", Templates.Names));
                return 0;
            }
            if (args[0] == "--version")
            {
                _output.WriteLine(Version);
                return 0;
            }

            var arguments = new Arguments(args[1..]);
            if (arguments.TakeFlag("--help") || arguments.TakeFlag("-h"))
            {
                _output.WriteLine(Help.Text);
                _output.WriteLine("Templates: " + string.Join(", ", Templates.Names));
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
                    string project = GatewayDiscovery.ResolveProject(_workingDirectory, projectOption);
                    string stateRoot = GatewayDiscovery.GetStateRoot(project, stateOption, _workingDirectory);
                    string app = GatewayDiscovery.ResolveApplication(project, stateRoot, appOption);
                    var state = new LocalStateCommands(_input, _output, _http, _environment ?? Environment.GetEnvironmentVariable);
                    return args[0] == "parameter"
                        ? await state.ParameterAsync(arguments, stateRoot, app, cancellationToken).ConfigureAwait(false)
                        : await state.StatusAsync(arguments, stateRoot, app, cancellationToken).ConfigureAwait(false);
                case "login":
                    return await new DeviceLogin(_http, _output, _error, _homeDirectory, _delay, _timeProvider)
                        .ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
                default:
                    throw new CliException("Unknown command. Use cohesion --help.");
            }
        }
        catch (CliException exception)
        {
            _error.WriteLine(exception.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _error.WriteLine("Cancelled.");
            return 130;
        }
        catch (HttpRequestException)
        {
            _error.WriteLine("HTTP request failed. Check that the endpoint is available and its certificate is trusted.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            _error.WriteLine("HTTP request timed out.");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or XmlException or Win32Exception or ArgumentException or FormatException)
        {
            // Serialization and OS diagnostics can contain credential values or request URLs.
            _error.WriteLine("Unable to read or write the requested data, or start dotnet. Check the path, format, permissions and .NET installation.");
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
            _error.WriteLine($"Cohesion templates are not installed. Run: dotnet new install {Templates.PackageId}::{Version}");
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
            ? GatewayDiscovery.ResolveProject(_workingDirectory, null)
            : Path.GetFullPath(selected, _workingDirectory));
        return DotnetAsync(inContainer
            ? ["publish", project, "-t:CohesionPublishImage", "-p:_CohesionImageInContainer=true", .. args.Remaining]
            : ["publish", project, .. args.Remaining], cancellationToken);
    }

    private Task<int> GatewayAsync(string verb, Arguments args, CancellationToken cancellationToken = default)
    {
        string? projectOption = args.TakeValue("--project");
        string? gateway = args.TakeValue("--gateway");
        Func<string, string?> readEnvironment = _environment ?? Environment.GetEnvironmentVariable;
        bool hasEnvironmentVariable =
            !string.IsNullOrWhiteSpace(readEnvironment("COHESION_ENVIRONMENT"))
            || !string.IsNullOrWhiteSpace(readEnvironment("DOTNET_ENVIRONMENT"));
        var mapped = new List<string>();
        if (gateway is not null)
        {
            mapped.AddRange(["--gateway", gateway]);
        }
        if (verb == "deploy")
        {
            mapped.AddRange(["--mode", "apply"]);
        }
        if (verb == "run" && !hasEnvironmentVariable)
        {
            string[] remaining = args.Remaining;
            bool hasEnvironmentArgument = Array.Exists(remaining, argument =>
                string.Equals(argument, "--environment", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--environment=", StringComparison.OrdinalIgnoreCase));
            // Passthrough options retain the gateway parser's last-value precedence.
            string? effectiveGateway = gateway;
            for (int index = 0; index < remaining.Length; index++)
            {
                string argument = remaining[index];
                if (string.Equals(argument, "--gateway", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveGateway = index + 1 < remaining.Length ? remaining[++index] : string.Empty;
                }
                else if (argument.StartsWith("--gateway=", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveGateway = argument["--gateway=".Length..];
                }
            }
            if (!hasEnvironmentArgument && (effectiveGateway is null
                || string.Equals(effectiveGateway, "local", StringComparison.OrdinalIgnoreCase)
                || string.Equals(effectiveGateway, "inprocess", StringComparison.OrdinalIgnoreCase)))
            {
                mapped.AddRange(["--environment", "Local"]);
            }
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
        string project = GatewayDiscovery.ResolveProject(_workingDirectory, projectOption);
        // SDK 10.0.401 retains the caller's cwd in the verified dotnet-run probe.
        // Set it explicitly so gateway state and relative arguments are project-local.
        // dotnet launch profiles override inherited variables; preserve an explicit shell environment.
        string[] launchOptions = hasEnvironmentVariable ? ["--no-launch-profile"] : [];
        return _processes.RunAsync("dotnet", ["run", "--project", project, .. launchOptions, "--", .. mapped, .. args.Remaining],
            Path.GetDirectoryName(project)!, cancellationToken);
    }

    private Task<int> DotnetAsync(string[] arguments, CancellationToken cancellationToken = default) =>
        _processes.RunAsync("dotnet", arguments, _workingDirectory, cancellationToken);
}
