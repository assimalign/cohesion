using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Database.ApplicationModel;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Testing.Tests;

/// <summary>
/// Real-process coverage for the SDK-enabled database sample, including its generated
/// manifest, LocalGateway carrier, typed SQL wire client, durable mount, and admin plane.
/// </summary>
public sealed class DatabaseSampleHostEndToEndTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);
    private readonly string _statePath = Path.Combine(
        Path.GetTempPath(),
        "cohesion-database-sample-e2e",
        Guid.NewGuid().ToString("N"));

    /// <summary>Removes the isolated LocalGateway state after each test.</summary>
    public void Dispose()
    {
        if (!Directory.Exists(_statePath))
        {
            return;
        }

        try
        {
            Directory.Delete(_statePath, recursive: true);
        }
        catch (IOException)
        {
            // The gateway already owns lifecycle cleanup; fixture cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // The gateway already owns lifecycle cleanup; fixture cleanup is best effort.
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Testing] - SDK sample: emits the Database manifest and generated resource sources")]
    public void Build_EnabledSampleHost_ShouldEmitDatabaseManifest()
    {
        // Arrange
        string manifestPath = GetSampleGeneratedPath("resource.json");

        // Act
        ResourceManifest manifest = ResourceManifest.Load(manifestPath);

        // Assert
        manifest.Schema.ShouldBe(ResourceManifest.SchemaV1);
        manifest.Name.ToString().ShouldBe("sample-database");
        manifest.Application.ToString().ShouldBe("database-tests");
        manifest.Kind.ShouldBe("Database");
        manifest.ApplicationModel.ShouldBe("Assimalign.Cohesion.Database.ApplicationModel");
        manifest.Artifact.Composable.ShouldBeTrue();
        manifest.Artifact.AppHost.ShouldNotBeNullOrWhiteSpace();
        string appHost = ResolveSampleAppHost(manifest.Artifact.AppHost!);
        File.Exists(appHost).ShouldBeTrue(
            $"the generated sample apphost does not exist: {manifest.Artifact.AppHost}");

        ResourceManifestEndpoint database = manifest.Endpoints.Single(endpoint => endpoint.Name == "db");
        database.Scheme.ShouldBe("cohesion-db");
        database.Protocol.ShouldBe("tcp");
        database.ContainerPort.ShouldBe(5740);
        database.Public.ShouldBeFalse();

        ResourceManifestEndpoint admin = manifest.Endpoints.Single(endpoint => endpoint.Name == "admin");
        admin.Scheme.ShouldBe("http");
        admin.ContainerPort.ShouldBe(8081);
        admin.Public.ShouldBeFalse();

        manifest.Probes.Readiness.ShouldNotBeNull();
        manifest.Probes.Readiness!.Endpoint.ShouldBe("admin");
        manifest.Probes.Readiness.Http.ShouldBe("/readyz");
        manifest.Probes.Liveness.ShouldNotBeNull();
        manifest.Probes.Liveness!.Endpoint.ShouldBe("admin");
        manifest.Probes.Liveness.Http.ShouldBe("/livez");
        manifest.ControlPlane.Endpoint.ShouldBe("admin");
        manifest.ControlPlane.Path.ShouldBe("/cohesion/v1");

        ResourceManifestMount data = manifest.Mounts.ShouldHaveSingleItem();
        data.Name.ShouldBe("data");
        data.Kind.ShouldBe(ResourceMountKind.Volume);
        data.ContainerPath.ShouldBe("/data");
        data.Size.ShouldBe("10Gi");
        manifest.Lifecycle.Workload.ShouldBe(WorkloadKind.StatefulSet);
        manifest.Lifecycle.Replicas.ShouldBe(1);
        manifest.Lifecycle.MaxReplicas.ShouldBe(1);

        File.ReadAllText(GetSampleGeneratedPath("Resource.g.cs"))
            .ShouldContain("ResourceRuntime.Current.GetEndpoint");
        File.ReadAllText(GetSampleGeneratedPath("ResourceControlPlane.g.cs"))
            .ShouldContain("DatabaseResourceControlPlane.Create()");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Testing] - Gateway E2E: launches the generated apphost, serves admin and SQL, and recovers durable data")]
    public async Task LocalGateway_WithGeneratedSampleManifest_ShouldServeAndRecoverAcrossRelaunch()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TestTimeout);
        ResourceManifest generatedManifest = ResourceManifest.Load(
            GetSampleGeneratedPath("resource.json"));
        Directory.CreateDirectory(_statePath);

        // Bootstrap issuance and authenticated default probing belong to the gateway. Supply
        // those upstream inputs explicitly so this fixture remains a #973-owned Database E2E.
        ResourceManifest manifest = PrepareRuntimeManifest(generatedManifest);

        LocalGateway firstGateway = CreateGateway();
        IApplicationModel firstModel = BuildModel(manifest, firstGateway);

        // Act: launch the real SDK-produced apphost and drive both exposed planes.
        await ((IApplicationGateway)firstGateway).StartAsync(firstModel, cancellation.Token);

        try
        {
            (Uri database, Uri admin) = await ReadEndpointsAsync(
                manifest,
                cancellation.Token);
            await AssertAdminPlaneAsync(manifest, admin, cancellation.Token);
            await SeedDatabaseAsync(database, cancellation.Token);
        }
        finally
        {
            await ((IApplicationGateway)firstGateway).StopAsync(cancellation.Token);
        }

        LocalGateway secondGateway = CreateGateway();
        IApplicationModel secondModel = BuildModel(manifest, secondGateway);
        await ((IApplicationGateway)secondGateway).StartAsync(secondModel, cancellation.Token);

        try
        {
            (Uri database, Uri admin) = await ReadEndpointsAsync(
                manifest,
                cancellation.Token);

            // Assert: LocalGateway reused the durable volume and the typed client sees the rows.
            await AssertRecoveredDatabaseAsync(database, cancellation.Token);

            using var client = new HttpClient { BaseAddress = admin };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                ReadBootstrapCredential(manifest));
            using HttpResponseMessage response = await client.PostAsync(
                "/cohesion/v1/stop",
                content: null,
                cancellation.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }
        finally
        {
            await ((IApplicationGateway)secondGateway).StopAsync(cancellation.Token);
        }
    }

    private LocalGateway CreateGateway()
    {
        return new LocalGateway(new LocalGatewayOptions
        {
            BaseDirectory = Path.GetDirectoryName(
                ResolveSampleAppHost(
                    ResourceManifest.Load(GetSampleGeneratedPath("resource.json")).Artifact.AppHost!)),
            StateDirectory = _statePath,
            ProbeInterval = TimeSpan.FromMilliseconds(100),
            ProbeTimeout = TimeSpan.FromSeconds(2),
            ReadinessBudget = TimeSpan.FromSeconds(45),
            StopGrace = TimeSpan.FromSeconds(30),
        });
    }

    private static IApplicationModel BuildModel(
        ResourceManifest manifest,
        LocalGateway gateway)
    {
        IApplicationBuilder builder = Application
            .CreateBuilder(
                manifest.Application,
                ["--environment", "Testing"])
            .UseGateway(gateway);
        builder.AddDatabase(manifest);
        return builder.Build().Model;
    }

    private async Task<(Uri Database, Uri Admin)> ReadEndpointsAsync(
        ResourceManifest manifest,
        CancellationToken cancellationToken)
    {
        string portsPath = Path.Combine(
            _statePath,
            manifest.Application.ToString(),
            ".state",
            "ports.json");
        await using FileStream stream = File.OpenRead(portsPath);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        JsonElement ports = document.RootElement
            .GetProperty("resources")
            .GetProperty(manifest.Name.ToString());

        ResourceManifestEndpoint databaseManifest = manifest.Endpoints.Single(
            endpoint => endpoint.Name == "db");
        ResourceManifestEndpoint adminManifest = manifest.Endpoints.Single(
            endpoint => endpoint.Name == "admin");
        Uri database = Uri.CreateEndpoint(
            databaseManifest.Scheme,
            "127.0.0.1",
            ports.GetProperty("db").GetInt32());
        Uri admin = Uri.CreateEndpoint(
            adminManifest.Scheme,
            "127.0.0.1",
            ports.GetProperty("admin").GetInt32());
        return (database, admin);
    }

    private async Task AssertAdminPlaneAsync(
        ResourceManifest manifest,
        Uri admin,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = admin };

        foreach (string path in new[] { "/healthz", "/readyz", "/livez" })
        {
            using HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        using HttpResponseMessage anonymousResponse = await client.GetAsync(
            "/cohesion/v1/endpoints",
            cancellationToken);
        anonymousResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        string credential = ReadBootstrapCredential(manifest);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            credential);

        using HttpResponseMessage endpointsResponse = await client.GetAsync(
            "/cohesion/v1/endpoints",
            cancellationToken);
        endpointsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument endpoints = JsonDocument.Parse(
            await endpointsResponse.Content.ReadAsStringAsync(cancellationToken));
        JsonElement endpointMap = endpoints.RootElement.GetProperty("endpoints");
        endpointMap.TryGetProperty("admin", out _).ShouldBeTrue();
        endpointMap.TryGetProperty("db", out _).ShouldBeTrue();

        using HttpResponseMessage commandsResponse = await client.GetAsync(
            "/cohesion/v1/commands",
            cancellationToken);
        commandsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument commands = JsonDocument.Parse(
            await commandsResponse.Content.ReadAsStringAsync(cancellationToken));
        commands.RootElement.GetProperty("acceptedCommandKinds").GetArrayLength().ShouldBe(0);
    }

    private string ReadBootstrapCredential(ResourceManifest manifest)
    {
        string credentialPath = GetBootstrapCredentialPath(manifest);
        byte[] content = new Assimalign.Cohesion.Hosting.ResourceMount(credentialPath).ReadAllBytes();
        try
        {
            return Encoding.UTF8.GetString(content);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private ResourceManifest PrepareRuntimeManifest(ResourceManifest manifest)
    {
        ResourceManifestProbe startup = manifest.Probes.Readiness
            ?? throw new InvalidOperationException(
                "The generated Database sample manifest must declare its readiness probe.");
        string credentialPath = GetBootstrapCredentialPath(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);

        byte[] credential = Encoding.UTF8.GetBytes("database-sample-e2e-bootstrap");
        try
        {
            WriteBootstrapCredential(credentialPath, credential);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }

        var environment = new Dictionary<string, string>(
            manifest.EnvironmentVariables,
            StringComparer.Ordinal)
        {
            [ResourceEnvironment.BootstrapTokenPath] = credentialPath,
        };

        return manifest with
        {
            EnvironmentVariables = environment,
            Probes = manifest.Probes with { Startup = startup },
        };
    }

    private string GetBootstrapCredentialPath(ResourceManifest manifest)
    {
        return Path.Combine(
            _statePath,
            manifest.Application.ToString(),
            manifest.Name.ToString(),
            ".state",
            "bootstrap.token");
    }

    private static void WriteBootstrapCredential(string path, byte[] credential)
    {
        if (OperatingSystem.IsWindows())
        {
            byte[] protectedCredential = ProtectedData.Protect(
                credential,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            try
            {
                File.WriteAllBytes(path, protectedCredential);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedCredential);
            }

            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using var stream = new FileStream(path, options);
        stream.Write(credential);
    }

    private static async Task SeedDatabaseAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        await using ISqlClient client = CreateSqlClient(endpoint);
        await using ISqlConnection connection = await client.ConnectAsync(cancellationToken);

        await connection.ExecuteAsync(
            "CREATE TABLE orders (id INT NOT NULL, item VARCHAR(100))",
            cancellationToken: cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO orders (id, item) VALUES (1, 'widget'), (2, 'gadget')",
            cancellationToken: cancellationToken);

        SqlResultSet rows = await connection.QueryAsync(
            "SELECT id, item FROM orders ORDER BY id",
            cancellationToken: cancellationToken);
        rows.Count.ShouldBe(2);
        rows[0].GetString("item").ShouldBe("widget");
    }

    private static async Task AssertRecoveredDatabaseAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        await using ISqlClient client = CreateSqlClient(endpoint);
        await using ISqlConnection connection = await client.ConnectAsync(cancellationToken);

        SqlResultSet rows = await connection.QueryAsync(
            "SELECT id, item FROM orders ORDER BY id",
            cancellationToken: cancellationToken);
        rows.Count.ShouldBe(2);
        rows[1].GetString("item").ShouldBe("gadget");
    }

    private static ISqlClient CreateSqlClient(Uri endpoint)
    {
        return SqlClient.Create(new SqlClientOptions
        {
            Settings = DatabaseConnectionSettings.For(
                endpoint,
                database: "sample",
                principal: "gateway-e2e"),
            ConnectionFactory = new TcpConnectionFactory(),
        });
    }

    private static string GetSampleGeneratedPath(string fileName)
    {
        string baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string targetFramework = Path.GetFileName(baseDirectory);
        string configuration = Directory.GetParent(baseDirectory)?.Name
            ?? throw new InvalidOperationException("The test output configuration could not be determined.");
        string repository = FindRepositoryRoot();
        string path = Path.Combine(
            repository,
            "resources",
            "Database",
            "samples",
            "Assimalign.Cohesion.Database.SampleHost",
            "obj",
            configuration,
            targetFramework,
            "cohesion",
            fileName);

        File.Exists(path).ShouldBeTrue(
            $"the generated SDK sample artifact does not exist: {path}");
        return path;
    }

    private static string ResolveSampleAppHost(string appHost)
    {
        return Path.IsPathRooted(appHost)
            ? Path.GetFullPath(appHost)
            : Path.GetFullPath(appHost, GetSampleProjectDirectory());
    }

    private static string GetSampleProjectDirectory()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "resources",
            "Database",
            "samples",
            "Assimalign.Cohesion.Database.SampleHost");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"The Cohesion repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
