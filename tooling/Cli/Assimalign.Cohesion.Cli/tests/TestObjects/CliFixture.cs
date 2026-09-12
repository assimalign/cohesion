using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Cli.Tests;

internal sealed class CliFixture : IDisposable
{
    internal static string Repository
    {
        get
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "docs", "DEVELOPER_EXPERIENCE_DESIGN.md")))
                {
                    return directory.FullName;
                }
            }
            throw new InvalidOperationException("Tests must run inside the repository.");
        }
    }

    internal string Root { get; } = Path.Combine(Repository, "_out", "verify-40", "tests", Guid.NewGuid().ToString("N")[..8]);
    internal string Home => Path.Combine(Root, "home");
    internal string StateRoot => Path.Combine(Root, "Gateway", ".cohesion");
    internal string ApplicationDirectory => Path.Combine(StateRoot, "sample");
    internal StringWriter Output { get; } = new();
    internal StringWriter Error { get; } = new();
    internal RecordingProcessRunner Runner { get; } = new();
    internal HttpClient Http { get; }
    internal List<TimeSpan> Delays { get; } = [];
    internal TestClock Clock { get; } = new();

    internal CliFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null)
    {
        Directory.CreateDirectory(Root);
        Http = new HttpClient(new TestHttpHandler(handler ?? ((_, _) =>
            throw new InvalidOperationException("This test must not send HTTP requests."))));
    }

    internal string Write(string path, string contents)
    {
        string fullPath = Path.GetFullPath(Path.Combine(Root, path));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    internal string Gateway(string properties = "<CohesionApplicationName>sample</CohesionApplicationName>",
        string sdk = "Assimalign.Cohesion.Sdk.Gateway", string path = "Gateway/Gateway.csproj") =>
        Write(path, $"<Project Sdk=\"{sdk}\"><PropertyGroup>{properties}</PropertyGroup></Project>");

    internal CliApplication Application(string stdin = "", string? token = null) =>
        new(Runner, Http, new StringReader(stdin), Output, Error, Root, Home,
            name => name == "COHESION_TOKEN" ? token : null, DelayAsync, Clock);

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Clock.Advance(delay);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Http.Dispose();
        Output.Dispose();
        Error.Dispose();
        string allowed = Path.GetFullPath(Path.Combine(Repository, "_out", "verify-40", "tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cleanup outside the test root refused.");
        }
        Directory.Delete(Root, recursive: true);
    }

    internal static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };
}

internal sealed class RecordingProcessRunner : IProcessRunner
{
    internal List<(string Executable, IReadOnlyList<string> Arguments, string Directory)> Calls { get; } = [];
    internal int ExitCode { get; set; }

    public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((executable, arguments, workingDirectory));
        return Task.FromResult(ExitCode);
    }
}

internal sealed class TestHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan duration) => _now += duration;
}
