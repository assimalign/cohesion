using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry.Internal;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.OpenTelemetry;

namespace Assimalign.Cohesion.Hosting.Telemetry.Tests;

public sealed class ResourceTelemetryTests
{
    [Theory(DisplayName = "Cohesion Test [Hosting.Telemetry] - Off: preserves factory and provider shape")]
    [InlineData(null, "https://collector.test")]
    [InlineData("inprocess", null)]
    [InlineData("local", " ")]
    public void Configure_WithoutGate_ShouldMutateNothing(string? gateway, string? endpoint)
    {
        using var scope = ResourceRuntime.CreateScope(Context(gateway, endpoint));
        var logging = new LoggerFactoryBuilder(); var console = new ConsoleLoggerProvider(); logging.AddProvider(console);
        ResourceTelemetry.Configure(ResourceRuntime.Current).ShouldBeNull();
        ResourceTelemetry.Configure(ResourceRuntime.Current, logging, out var lifetime).ShouldBeFalse();
        lifetime.ShouldBeNull();
        using var factory = logging.Build();
        factory.Providers.ShouldBe(new[] { console });
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Telemetry] - On: invocation values configure isolated providers")]
    [InlineData("local", null, 1)]
    [InlineData("inprocess", "json", 2)]
    [InlineData("inprocess", "text", 1)]
    public async Task Configure_WithGate_ShouldUseInvocationEnvironment(string gateway, string? format, int providers)
    {
        using var scope = ResourceRuntime.CreateScope(Context(gateway, "https://collector.test", format: format));
        using ILoggerFactory factory = ResourceTelemetry.Configure(ResourceRuntime.Current, out IHostService? lifetime)!;
        factory.Providers.Count.ShouldBe(providers);
        factory.Providers[0].Name.ShouldBe("OpenTelemetry");
        lifetime.ShouldNotBeNull();
        await lifetime.StopAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - Console: explicit provider wins over JSON default")]
    public async Task Configure_ExistingConsole_ShouldNotDuplicate()
    {
        var builder = new LoggerFactoryBuilder(); var console = new ConsoleLoggerProvider(); builder.AddProvider(console);
        ResourceTelemetry.Configure(Context("inprocess", "https://collector.test", format: "JSON"), builder, out IHostService? lifetime).ShouldBeTrue();
        using ILoggerFactory factory = builder.Build();
        factory.Providers.Count.ShouldBe(2); factory.Providers[0].ShouldBeSameAs(console);
        await lifetime!.StopAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Telemetry] - Protocol: unsupported values fail by name")]
    [InlineData("otlp-grpc")]
    [InlineData("unknown")]
    public void Configure_UnsupportedProtocol_ShouldRefuse(string protocol)
    {
        var context = Context("inprocess", "https://collector.test", protocol);
        Exception error = protocol == "otlp-grpc"
            ? Should.Throw<NotSupportedException>(() => ResourceTelemetry.Configure(context))
            : Should.Throw<InvalidOperationException>(() => ResourceTelemetry.Configure(context));
        error.Message.ShouldContain(nameof(ResourceEnvironment.TelemetryProtocol), Case.Sensitive);
        if (protocol == "otlp-grpc") { error.Message.ShouldContain("HTTP only", Case.Sensitive); }
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - Headers: empty, comments, CRLF and duplicate names")]
    public void ParseHeaders_WithDocument_ShouldPreserveValues()
    {
        ResourceTelemetry.ParseHeaders([]).ShouldBeEmpty();
        var headers = ResourceTelemetry.ParseHeaders(Encoding.UTF8.GetBytes("# comment\r\n\r\nX-Test: old\r\nx-test: value: remainder \n"));
        headers.Count.ShouldBe(1); headers["X-Test"].ShouldBe("value: remainder");
        Should.Throw<InvalidOperationException>(() => ResourceTelemetry.ParseHeaders("bad"u8));
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - Header file: invocation path reads an empty document without error")]
    public void CreateOptions_EmptyHeaderFile_ShouldHaveNoHeaders()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "headers-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, string.Empty);
        try
        {
            var context = new ResourceContext(applicationName: "app", resourceName: "web", environmentName: "Development", gatewayName: "inprocess",
                contentRootPath: null, endpoints: null, mounts: null, settings: null, references: null,
                bootstrapCredential: ReadOnlyMemory<byte>.Empty, applicationTrustKey: ReadOnlyMemory<byte>.Empty,
                ambientValues: new Dictionary<string, string?> { [ResourceEnvironment.TelemetryEndpoint] = "https://collector.test", [ResourceEnvironment.TelemetryHeadersPath] = path });
            ResourceTelemetry.CreateOptions(context).Headers.ShouldBeEmpty();
            byte[] headers = Encoding.UTF8.GetBytes("# comment\r\nAuthorization: Bearer test\r\n");
            File.WriteAllBytes(path, OperatingSystem.IsWindows()
                ? ProtectedData.Protect(headers, null, DataProtectionScope.CurrentUser) : headers);
            ResourceTelemetry.CreateOptions(context).Headers["Authorization"].ShouldBe("Bearer test");
        }
        finally { File.Delete(path); }
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - JSON console writes one escaped object per line")]
    public void ConsoleFormatter_MessageWithNewline_ShouldProduceOneLine()
    {
        using var output = new StringWriter();
        JsonConsoleFormatter.Write(new LoggerEntry(LogLevel.Information, "category", "line one\nline two"), output);
        string json = output.ToString();
        json.Count(character => character == '\n').ShouldBe(1);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("message").GetString().ShouldBe("line one\nline two");
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - Severity: all eight Logging levels have explicit semantics")]
    public void Severity_AllLevels_ShouldMatchTable()
    {
        Enum.GetValues<LogLevel>().Length.ShouldBe(8);
        var expected = new[] { (1, "TRACE"), (5, "DEBUG"), (9, "INFO"), (13, "WARN"), (17, "ERROR"), (21, "FATAL"), (10, "EVENT") };
        for (int index = 0; index < expected.Length; index++) { OtlpLoggerProvider.Severity((LogLevel)index).ShouldBe(expected[index]); }
        Should.Throw<ArgumentOutOfRangeException>(() => OtlpLoggerProvider.Severity(LogLevel.None));
        Should.Throw<ArgumentOutOfRangeException>(() => OtlpLoggerProvider.Severity((LogLevel)99));
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - Stop: final batch flushes without factory disposal")]
    public async Task StopAsync_WithPendingLog_ShouldFlushLastBatch()
    {
        var handler = new TestHandler();
        IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(new OtlpExporterOptions
        { Endpoint = new Uri("https://collector.test"), HandlerFactory = () => handler, FlushInterval = TimeSpan.FromDays(1) });
        var provider = new OtlpLoggerProvider(exporter);
        var service = new TelemetryHostService(provider);
        provider.Create("category").Log(new LoggerEntry(LogLevel.Event, "category", "just before stop", new InvalidOperationException("failure")));
        handler.Body.ShouldBeNull();
        await service.StopAsync(CancellationToken.None);
        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldContain("just before stop", Case.Sensitive);
        handler.Body.ShouldContain("exception.type", Case.Sensitive);
        handler.Body.ShouldContain("cohesion.log.id", Case.Sensitive);
        handler.Body.ShouldContain("\"severityNumber\":10", Case.Sensitive);
        provider.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - Collector: failure cannot fail log or stop")]
    public async Task StopAsync_UnreachableCollector_ShouldRemainBounded()
    {
        IOtlpLogExporter exporter = OtlpExporter.CreateLogExporter(new OtlpExporterOptions
        { Endpoint = new Uri("https://collector.test"), HandlerFactory = () => new TestHandler { Fail = true }, Timeout = TimeSpan.FromMilliseconds(100) });
        var provider = new OtlpLoggerProvider(exporter);
        provider.Create("test").Log(new LoggerEntry(LogLevel.Information, "test", "safe"));
        await new TelemetryHostService(provider).StopAsync(CancellationToken.None);
        exporter.FailedExportCount.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Telemetry] - Stop honors an expired host budget and still starts teardown")]
    public async Task StopAsync_CancelledBudget_ShouldStillDisposeExporter()
    {
        var exporter = new StalledExporter();
        var service = new TelemetryHostService(new OtlpLoggerProvider(exporter));
        using var budget = new CancellationTokenSource();
        budget.Cancel();
        await service.StopAsync(budget.Token).WaitAsync(TimeSpan.FromSeconds(1));
        exporter.Disposed.ShouldBeTrue();
        exporter.Pending.SetResult();
    }

    private sealed class StalledExporter : IOtlpLogExporter
    {
        internal TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public long DroppedCount => 0;
        public long FailedExportCount => 0;
        public bool TryEnqueue(OtlpLogRecord record) => true;
        public ValueTask<bool> ExportAsync(ReadOnlyMemory<OtlpLogRecord> batch, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => new(Pending.Task);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static ResourceContext Context(string? gateway, string? endpoint, string? protocol = null, string? format = null) => new(
        applicationName: "app", resourceName: "api", environmentName: "Development", gatewayName: gateway,
        contentRootPath: null, endpoints: null, mounts: null, settings: null, references: null,
        bootstrapCredential: ReadOnlyMemory<byte>.Empty, applicationTrustKey: ReadOnlyMemory<byte>.Empty,
        ambientValues: new Dictionary<string, string?> { [ResourceEnvironment.TelemetryEndpoint] = endpoint,
            [ResourceEnvironment.TelemetryProtocol] = protocol, [ResourceEnvironment.LogFormat] = format });

    private sealed class TestHandler : HttpMessageHandler
    {
        internal string? Body { get; private set; }
        internal bool Fail { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) { throw new HttpRequestException("unreachable"); }
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"partialSuccess\":{}}") };
        }
    }
}
