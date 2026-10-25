using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Testing.Tests;

/// <summary>Real packaged-SDK proof of public TCP composition for the three #1391 models.</summary>
public sealed class DatabaseTcpSdkHostTests
{
    private const string fixtureName = "Assimalign.Cohesion.Database.TcpSdkHost";

    [Fact(DisplayName = "Cohesion Test [Database.Testing] - SDK TCP: compiler sees public Database.Tcp while its TCP driver stays runtime-private (#1391)")]
    public void Build_SdkOnlyConsumer_ShouldExposeTcpAndHideDriver()
    {
        // Arrange: these are evaluated compiler inputs produced by the real SDK build.
        string directory = GetFixtureDirectory();
        string closurePath = Path.Combine(directory, "tcp-sdk-compiler-closure.txt");
        File.Exists(closurePath).ShouldBeTrue($"The SDK compiler closure was not copied: {closurePath}");
        string[][] references = File.ReadAllLines(closurePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('|', 2))
            .ToArray();

        // Act / Assert: the parent test project's direct references cannot satisfy these checks.
        references.ShouldAllBe(reference => reference.Length == 2);
        string[] tcp = references.Where(reference => reference[0] == "Assimalign.Cohesion.Database.Tcp")
            .ShouldHaveSingleItem();
        tcp[1].Replace('\\', '/').ToLowerInvariant()
            .ShouldContain("/assimalign.cohesion.app.database.ref/", Case.Sensitive);
        references.Select(reference => reference[0]).ShouldNotContain("Assimalign.Cohesion.Connections.Tcp");
        File.Exists(Path.Combine(directory, "Assimalign.Cohesion.Connections.Tcp.dll")).ShouldBeTrue(
            "App.Database must carry the listener driver at runtime without making it a compiler reference.");
        File.Exists(Path.Combine(directory, "Assimalign.Cohesion.Database.Tcp.dll")).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Testing] - SDK TCP: each model authenticates and answers Ping through ordinary AddServer composition (#1391)")]
    [InlineData(0, "KeyValuePair")]
    [InlineData(1, "Graph")]
    [InlineData(2, "Blob")]
    public async Task Start_ModelListener_ShouldAuthenticateAndAnswerPing(int model, string modelName)
    {
        // Arrange: ninety seconds is a hang guard, not a response-time assertion.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string directory = GetFixtureDirectory();
        string appHost = Path.Combine(directory, fixtureName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        File.Exists(appHost).ShouldBeTrue($"The SDK apphost was not copied: {appHost}");
        var start = new ProcessStartInfo
        {
            FileName = appHost,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the TCP SDK consumer.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string>? remainingOutput = null;
        try
        {
            // StartAsync has completed for all three real servers before the child writes READY.
            string ready = await ReadReadyAsync(process.StandardOutput, stderr, timeout.Token);
            string[] fields = ready.Split('|');
            fields.Length.ShouldBe(4, ready);
            int[] ports = fields.Skip(1).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            ports.ShouldAllBe(port => port > 0);
            ports.Distinct().Count().ShouldBe(3);
            remainingOutput = process.StandardOutput.ReadToEndAsync();
            ProtocolMessageFamily[] families = [KeyValueProtocol.Family, GraphProtocol.Family, BlobProtocol.Family];

            // Act / Assert: RentAsync proves Startup/authentication/Ready for the selected model;
            // the generic exchange is the public Ping seam, independent of typed model commands.
            Uri endpoint = Uri.CreateEndpoint("cohesion-db", "127.0.0.1", ports[model]);
            {
                await using DatabaseClient client = DatabaseClient.Create(new DatabaseClientOptions
                {
                    Settings = DatabaseConnectionSettings.For(endpoint, "app", "tcp-sdk"),
                    ConnectionFactory = new TcpConnectionFactory(),
                    Family = families[model],
                });
                await using DatabaseConnection connection = await client.RentAsync(timeout.Token);
                connection.Database.ShouldBe("app");
                connection.Principal.ShouldBe("tcp-sdk");
                connection.ServerVersion.ShouldBe(ProtocolVersion.Current);
                connection.Family.ShouldBeSameAs(families[model]);
                (await connection.ExecuteAsync(new PingExchange(families[model]), timeout.Token))
                    .ShouldBe(ProtocolMessageType.Pong, $"{modelName} must serve the authenticated Ping.");
                connection.IsOpen.ShouldBeTrue();
            }

            await process.StandardInput.WriteLineAsync("STOP".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await remainingOutput.WaitAsync(timeout.Token);
            string error = await stderr.WaitAsync(timeout.Token);
            process.ExitCode.ShouldBe(0, $"SDK fixture output:\n{ready}\n{output}\n{error}");
            output.ShouldContain("STOPPED", Case.Sensitive);
        }
        finally
        {
            await StopProcessAsync(process);
            // Drain both pipes after the child exits, including failed/cancelled test paths.
            _ = await stderr;
            if (remainingOutput is not null) { _ = await remainingOutput; }
        }
    }

    private static string GetFixtureDirectory()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "TcpSdkHost");
        Directory.Exists(directory).ShouldBeTrue($"The SDK fixture output was not copied: {directory}");
        return directory;
    }

    private static async Task<string> ReadReadyAsync(StreamReader output, Task<string> stderr,
        CancellationToken cancellationToken)
    {
        var preamble = new StringBuilder();
        while (await output.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("READY|", StringComparison.Ordinal)) { return line; }
            preamble.AppendLine(line);
        }
        throw new InvalidOperationException($"The SDK consumer exited before READY:\n{preamble}\n{await stderr.WaitAsync(cancellationToken)}");
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited) { return; }
        try { process.StandardInput.Close(); }
        catch (IOException)
        {
            // A child that already closed its input still owns normal host cleanup below.
        }
        // Independent cleanup hang guard. EOF asks the child to stop even after test cancellation.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cleanup.Token);
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            // Only this Process.Start product is terminated, never unrelated dotnet processes.
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The child completed its own cleanup between the timeout and Kill.
            }
            catch (Win32Exception) when (process.HasExited)
            {
                // The OS already reaped this child; a live child's failed kill still escapes.
            }
            using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(killed.Token);
        }
    }

    private sealed class PingExchange : DatabaseProtocolExchange<ProtocolMessageType>
    {
        public PingExchange(ProtocolMessageFamily family) : base(family) { }

        protected override async ValueTask<ProtocolMessageType> ExecuteCoreAsync(
            ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            ProtocolFrame? response = await reader.ReadFrameAsync(cancellationToken);
            return response?.Type ?? throw new ProtocolException("The TCP SDK server closed before Pong.");
        }
    }
}
