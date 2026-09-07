using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalPortStore
{
    private const string loopbackHost = "127.0.0.1";

    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalPortStore(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public async Task<IReadOnlyList<ResourceEndpoint>> ResolveAsync(
        ApplicationName application,
        ResourceName resource,
        IReadOnlyList<ResourceEndpoint> endpoints,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        if (endpoints.Count == 0)
        {
            return Array.Empty<ResourceEndpoint>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string applicationDirectory = GetApplicationDirectory(application);
            string path = Path.Combine(applicationDirectory, "ports.json");
            PortAllocationDocument document = await LoadAsync(path, cancellationToken).ConfigureAwait(false);

            string resourceName = resource.ToString();
            if (!document.Resources.TryGetValue(resourceName, out Dictionary<string, int>? resourcePorts))
            {
                resourcePorts = new Dictionary<string, int>(StringComparer.Ordinal);
                document.Resources.Add(resourceName, resourcePorts);
            }

            HashSet<int> allocated = CollectAllocatedPorts(document, resourceName);
            var endpointVariables = new HashSet<string>(StringComparer.Ordinal);
            var observed = new ResourceEndpoint[endpoints.Count];
            bool changed = false;

            for (int index = 0; index < endpoints.Count; index++)
            {
                ResourceEndpoint endpoint = endpoints[index];
                string hostVariable = ResourceEnvironment.Endpoint(endpoint.Name, "HOST");
                string portVariable = ResourceEnvironment.Endpoint(endpoint.Name, "PORT");
                string schemeVariable = ResourceEnvironment.Endpoint(endpoint.Name, "SCHEME");

                if (!endpointVariables.Add(hostVariable))
                {
                    throw new InvalidDataException(
                        $"Endpoint '{endpoint.Name}' collides with another endpoint after environment-name normalization.");
                }

                string host = ReadOverride(environment, hostVariable) ?? loopbackHost;
                string scheme = ReadOverride(environment, schemeVariable) ?? endpoint.Scheme;
                int port;

                if (environment.TryGetValue(portVariable, out string? configuredPort))
                {
                    if (!int.TryParse(configuredPort, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                        || port is < 1 or > 65535)
                    {
                        throw new InvalidDataException(
                            $"Environment variable '{portVariable}' must contain a port from 1 through 65535.");
                    }
                }
                else if (!resourcePorts.TryGetValue(endpoint.Name, out port))
                {
                    port = AllocatePort(allocated);
                }

                if (port is < 1 or > 65535)
                {
                    throw new InvalidDataException(
                        $"Persisted port '{port}' for resource '{resource}' endpoint '{endpoint.Name}' is invalid.");
                }

                if (allocated.Contains(port))
                {
                    throw new InvalidDataException(
                        $"Port '{port}' for resource '{resource}' endpoint '{endpoint.Name}' "
                        + "is already allocated to another local endpoint.");
                }

                allocated.Add(port);
                if (!resourcePorts.TryGetValue(endpoint.Name, out int persistedPort) || persistedPort != port)
                {
                    resourcePorts[endpoint.Name] = port;
                    changed = true;
                }

                environment.TryAdd(hostVariable, host);
                environment.TryAdd(portVariable, port.ToString(CultureInfo.InvariantCulture));
                environment.TryAdd(schemeVariable, scheme);

                var address = new EndpointAddress(scheme, host, port);
                if (endpoint.IsPublic)
                {
                    environment.TryAdd(
                        ResourceEnvironment.Endpoint(endpoint.Name, "PUBLIC_URL"),
                        address.ToString());
                }

                observed[index] = new ResourceEndpoint(
                    endpoint.Name,
                    address.Scheme,
                    address.Port,
                    endpoint.IsPublic,
                    address.Host);
            }

            if (changed || !File.Exists(path))
            {
                Directory.CreateDirectory(applicationDirectory);
                await SaveAsync(path, document, cancellationToken).ConfigureAwait(false);
            }

            return observed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetApplicationDirectory(ApplicationName application)
    {
        string directory = Path.GetFullPath(
            Path.Combine(_stateDirectory, application.ToString(), ".state"));
        string root = _stateDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _stateDirectory
            : _stateDirectory + Path.DirectorySeparatorChar;

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!directory.StartsWith(root, comparison))
        {
            throw new InvalidDataException($"Application name '{application}' cannot be used as a state directory.");
        }

        return directory;
    }

    private static string? ReadOverride(IDictionary<string, string> environment, string variable)
    {
        if (!environment.TryGetValue(variable, out string? value))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Environment variable '{variable}' must not be empty.");
        }

        return value;
    }

    private static HashSet<int> CollectAllocatedPorts(PortAllocationDocument document, string resource)
    {
        var allocated = new HashSet<int>();
        foreach ((string currentResource, Dictionary<string, int> ports) in document.Resources)
        {
            if (string.Equals(currentResource, resource, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (int port in ports.Values)
            {
                if (port is >= 1 and <= 65535)
                {
                    allocated.Add(port);
                }
            }
        }

        return allocated;
    }

    private static int AllocatePort(ISet<int> allocated)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (!allocated.Contains(port))
                {
                    return port;
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new IOException("Could not allocate an unused loopback port after 100 attempts.");
    }

    private static async Task<PortAllocationDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new PortAllocationDocument();
        }

        await using FileStream stream = File.OpenRead(path);
        PortAllocationDocument? document = await JsonSerializer.DeserializeAsync(
            stream,
            LocalGatewayJsonContext.Default.PortAllocationDocument,
            cancellationToken).ConfigureAwait(false);

        return document ?? throw new InvalidDataException($"Port allocation file '{path}' is empty.");
    }

    private static async Task SaveAsync(
        string path,
        PortAllocationDocument document,
        CancellationToken cancellationToken)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    LocalGatewayJsonContext.Default.PortAllocationDocument,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class PortAllocationDocument
{
    public Dictionary<string, Dictionary<string, int>> Resources { get; init; } =
        new(StringComparer.Ordinal);
}
