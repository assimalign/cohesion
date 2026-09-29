using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Cli.Internal;

internal sealed class LocalStateCommands
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly HttpClient _http;
    private readonly Func<string, string?> _environment;

    /// <summary>Initializes a new instance of the <see cref="LocalStateCommands"/> class.</summary>
    /// <param name="input">The reader for standard input, used by parameter set --stdin.</param>
    /// <param name="output">The writer for standard output.</param>
    /// <param name="http">The HTTP client used for live control-plane status requests.</param>
    /// <param name="environment">The environment variable reader used to resolve COHESION_TOKEN.</param>
    public LocalStateCommands(
        TextReader input, TextWriter output, HttpClient http, Func<string, string?> environment)
    {
        _input = input;
        _output = output;
        _http = http;
        _environment = environment;
    }

    internal async Task<int> ParameterAsync(Arguments args, string stateRoot, string app,
        CancellationToken cancellationToken = default)
    {
        string? verb = args.TakeFirst();
        bool stdin = args.TakeFlag("--stdin");
        string? name = args.TakeFirst();
        string? value = args.TakeFirst(allowOptionLike: true);
        args.RequireEmpty();
        if (verb is not ("set" or "list" or "remove") ||
            (verb == "list" && (name is not null || stdin)) ||
            (verb == "remove" && (string.IsNullOrWhiteSpace(name) || value is not null || stdin)) ||
            (verb == "set" && (string.IsNullOrWhiteSpace(name) || (stdin == (value is not null)))))
        {
            throw new CliException("Use parameter set <name> <value|--stdin>, parameter list, or parameter remove <name>.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(stateRoot, app, "parameters.json");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            byte[] plaintext = ProtectedFile.Read(path);
            try
            {
                // Match ReadParametersAsync, including its rejection of duplicate names.
                using JsonDocument document = JsonDocument.Parse(plaintext);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new CliException("parameters.json must contain a JSON object with string values.");
                }
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        !parameters.TryAdd(property.Name, property.Value.GetString()!))
                    {
                        throw new CliException("parameters.json must contain unique names and string values only.");
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        if (verb == "list")
        {
            foreach (string key in parameters.Keys.Order(StringComparer.Ordinal))
            {
                _output.WriteLine(key);
            }
            return 0;
        }
        if (verb == "set")
        {
            parameters[name!] = stdin
                ? await _input.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
                : value!;
        }
        else
        {
            parameters.Remove(name!);
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(parameters, StateJsonContext.Default.DictionaryStringString);
        try
        {
            ProtectedFile.Write(path, json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
        return 0;
    }

    internal async Task<int> StatusAsync(Arguments args, string stateRoot, string app,
        CancellationToken cancellationToken = default)
    {
        bool live = args.TakeFlag("--live");
        string? suppliedToken = args.TakeValue("--token");
        args.RequireEmpty();
        if (suppliedToken is not null && !live)
        {
            throw new CliException("--token requires status --live.");
        }
        string directory = Path.Combine(stateRoot, app);
        if (!Directory.Exists(directory))
        {
            throw new CliException("no local gateway state; run `cohesion run` first");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string exportPath = Path.Combine(directory, "export.json");
        if (!File.Exists(exportPath))
        {
            throw new CliException("no local gateway export; run `cohesion run` first");
        }
        ExportDocument export = Read(exportPath, StateJsonContext.Default.ExportDocument);
        string portsPath = Path.Combine(directory, ".state", "ports.json");
        PortDocument ports = File.Exists(portsPath)
            ? Read(portsPath, StateJsonContext.Default.PortDocument) : new PortDocument();
        string ownerPath = Path.Combine(directory, ".state", "owner");
        _output.WriteLine($"Application: {app} ({export.Environment}, {export.Version})");
        _output.WriteLine("File status: declared endpoints and allocated ports, not observed resource state.");
        _output.WriteLine("Owner: " + (File.Exists(ownerPath) ? File.ReadAllText(ownerPath).Trim() : "(unavailable)"));
        _output.WriteLine("Allocated control-plane port: " + (ports.ControlPlane?.ToString() ?? "(unavailable)"));

        Uri? endpoint = null;
        string? token = null;
        if (live)
        {
            string metadataPath = Path.Combine(directory, "control-plane.json");
            if (!File.Exists(metadataPath))
            {
                throw new CliException("No control-plane.json; start the gateway before using status --live.");
            }
            endpoint = HttpEndpoint.Parse(Read(metadataPath, StateJsonContext.Default.ControlPlaneDocument).Url);
            token = suppliedToken ?? _environment("COHESION_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new CliException("status --live requires --token or COHESION_TOKEN. Use a trust issue --developer token for this application (a missing bearer header receives HTTP 401).");
            }
        }
        foreach (ExportResource resource in export.Resources ?? throw new CliException("export.json has no resource list."))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resource is null)
            {
                throw new CliException("export.json contains an invalid resource.");
            }
            string name = GatewayDiscovery.PathSegment(resource.Name);
            string pidPath = Path.Combine(directory, ".state", name, "pid");
            string liveness = File.Exists(pidPath)
                ? GetLiveness(Read(pidPath, StateJsonContext.Default.ProcessDocument)) : "unavailable";
            _output.WriteLine($"{name}: kind={resource.Kind}, manifestHash={resource.ManifestHash}, local process={liveness}");
            foreach (ExportEndpoint declared in resource.Endpoints ?? [])
            {
                if (declared is null)
                {
                    throw new CliException("export.json contains an invalid endpoint.");
                }
                _output.WriteLine($"  declared {declared.Name}: internal={declared.Internal ?? "(none)"}, public={declared.Public ?? "(none)"}");
            }
            if ((ports.Resources ?? throw new CliException("ports.json has no resource allocation map."))
                .TryGetValue(name, out Dictionary<string, int>? allocated))
            {
                foreach ((string key, int port) in (allocated ?? throw new CliException("Invalid port allocation map."))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    _output.WriteLine($"  allocated {key}: {port}");
                }
            }
            if (endpoint is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/cohesion/v1/resources/" + Uri.EscapeDataString(name)));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new CliException($"status --live received HTTP {(int)response.StatusCode}. Use a trust issue --developer token for this application; IdentityHub login tokens are not accepted.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new CliException($"status --live received HTTP {(int)response.StatusCode}.");
                }
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                ObservedResource observed = await JsonSerializer.DeserializeAsync(stream,
                    StateJsonContext.Default.ObservedResource, cancellationToken).ConfigureAwait(false)
                    ?? throw new CliException("Empty control-plane response.");
                if (string.IsNullOrWhiteSpace(observed.State))
                {
                    throw new CliException("Control-plane response has no observed state.");
                }
                _output.WriteLine($"  observed state: {observed.State}");
                foreach (ObservedEndpoint address in observed.Endpoints ?? [])
                {
                    if (address is null)
                    {
                        throw new CliException("Control-plane response contains an invalid endpoint.");
                    }
                    _output.WriteLine($"  observed {address.Name}: {address.Address} (public={address.IsPublic})");
                }
            }
        }
        return 0;
    }

    private static T Read<T>(string path, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(path), typeInfo)
            ?? throw new CliException("Empty local state document.");

    internal static string GetLiveness(ProcessDocument registration)
    {
        if (registration.ProcessId <= 0 || registration.StartTimeUtcTicks <= 0)
        {
            return "not running";
        }
        try
        {
            using Process process = Process.GetProcessById(registration.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == registration.StartTimeUtcTicks
                ? "running" : "not running (pid/start time mismatch)";
        }
        catch (ArgumentException)
        {
            return "not running";
        }
        catch (InvalidOperationException)
        {
            return "not running";
        }
        catch (Win32Exception)
        {
            return "unknown (process access denied)";
        }
    }
}
