using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.TestHost;

internal static class Program
{
    private const string CanonicalReadyMarker = "cohesion-resource: ready";

    public static async Task<int> Main(string[] args)
    {
        if ((args.Length > 0 && IsExecProbeArgument(args[0]))
            || string.Equals(Environment.GetEnvironmentVariable("TEST_MODE"), "exec-probe", StringComparison.OrdinalIgnoreCase))
        {
            return RunExecProbe(args);
        }

        using var stopping = new CancellationTokenSource();
        using var stopSignals = new StopSignalSubscription(stopping);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopSignals.Observe();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (args.Length > 0 && string.Equals(args[0], "wait", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token).ConfigureAwait(false);
            }
            else
            {
                await RunServerAsync(stopping.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"test-host failure: {exception}");
            return 70;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RunServerAsync(CancellationToken cancellationToken)
    {
        string endpointName = Environment.GetEnvironmentVariable("TEST_ENDPOINT_NAME") ?? "http";
        string host = RequiredEnvironment(ResourceEnvironment.Endpoint(endpointName, "HOST"));
        int port = ParsePort(RequiredEnvironment(ResourceEnvironment.Endpoint(endpointName, "PORT")));
        IPAddress address = await ResolveAddressAsync(host, cancellationToken).ConfigureAwait(false);

        var listener = new TcpListener(address, port);
        listener.Start();

        try
        {
            StartDescendant(Environment.GetEnvironmentVariable("TEST_DESCENDANT_PID_PATH"));
            int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            int launchCount = IncrementLaunchCount(Environment.GetEnvironmentVariable("TEST_LAUNCH_COUNT_PATH"));

            CaptureEnvironment(Environment.GetEnvironmentVariable("TEST_ENV_CAPTURE_PATH"));
            CaptureMounts(Environment.GetEnvironmentVariable("TEST_MOUNT_CAPTURE_PATH"));
            WriteOptionalText(Environment.GetEnvironmentVariable("TEST_BOUND_PATH"), boundPort.ToString(CultureInfo.InvariantCulture));

            Console.Out.WriteLine(Environment.GetEnvironmentVariable("TEST_STDOUT_LINE") ?? "test-host stdout");
            Console.Error.WriteLine(Environment.GetEnvironmentVariable("TEST_STDERR_LINE") ?? "test-host stderr");
            Console.Out.Flush();
            Console.Error.Flush();

            string? markerGatePath = Environment.GetEnvironmentVariable("TEST_READY_MARKER_GATE_PATH");
            if (!string.IsNullOrWhiteSpace(markerGatePath))
            {
                await WaitForFileAsync(markerGatePath, cancellationToken).ConfigureAwait(false);
            }

            string? configuredMarker = Environment.GetEnvironmentVariable("TEST_READY_MARKER");
            string marker = configuredMarker ?? CanonicalReadyMarker;
            if (marker.Length > 0)
            {
                Console.Out.WriteLine(marker);
                Console.Out.Flush();
            }

            WriteOptionalText(
                Environment.GetEnvironmentVariable("TEST_STARTED_PATH"),
                launchCount.ToString(CultureInfo.InvariantCulture));

            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await HandleRequestAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
        }

        string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string method = parts.Length > 0 ? parts[0] : string.Empty;
        string target = parts.Length > 1 ? parts[1] : string.Empty;
        string path = target.Split('?', 2)[0];
        AppendRequest(method, target);

        int statusCode;
        if (!string.Equals(method, "GET", StringComparison.Ordinal)
            && !string.Equals(method, "HEAD", StringComparison.Ordinal))
        {
            statusCode = 405;
        }
        else if (IsReadyPath(path))
        {
            statusCode = ReadStatus(Environment.GetEnvironmentVariable("TEST_READY_STATUS_PATH"));
        }
        else if (IsLivePath(path))
        {
            statusCode = ReadStatus(Environment.GetEnvironmentVariable("TEST_LIVE_STATUS_PATH"));
        }
        else
        {
            statusCode = 404;
        }

        string response = FormattableString.Invariant(
            $"HTTP/1.1 {statusCode} {ReasonPhrase(statusCode)}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        byte[] bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsReadyPath(string path)
    {
        string configured = Environment.GetEnvironmentVariable("TEST_READY_PATH") ?? "/readyz";
        return string.Equals(path, configured, StringComparison.Ordinal)
            || string.Equals(path, "/cohesion/v1/readyz", StringComparison.Ordinal);
    }

    private static bool IsLivePath(string path)
    {
        string configured = Environment.GetEnvironmentVariable("TEST_LIVE_PATH") ?? "/livez";
        return string.Equals(path, configured, StringComparison.Ordinal)
            || string.Equals(path, "/cohesion/v1/livez", StringComparison.Ordinal);
    }

    private static int ReadStatus(string? statusPath)
    {
        if (string.IsNullOrWhiteSpace(statusPath))
        {
            return 200;
        }

        if (!File.Exists(statusPath))
        {
            return 503;
        }

        string value;
        try
        {
            value = ReadSharedText(statusPath).Trim();
        }
        catch (IOException)
        {
            return 503;
        }
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int statusCode)
            && statusCode is >= 100 and <= 599)
        {
            return statusCode;
        }

        return value.ToUpperInvariant() switch
        {
            "HEALTHY" or "READY" or "TRUE" => 200,
            "UNHEALTHY" or "FALSE" => 503,
            _ => 500,
        };
    }

    private static int RunExecProbe(string[] args)
    {
        WriteOptionalText(
            Environment.GetEnvironmentVariable("TEST_EXEC_PROBE_CAPTURE_PATH"),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        int pathIndex = args.Length > 0 && IsExecProbeArgument(args[0]) ? 1 : 0;
        string? statusPath = args.Length > pathIndex
            ? args[pathIndex]
            : Environment.GetEnvironmentVariable("TEST_EXEC_PROBE_STATUS_PATH");

        if (string.IsNullOrWhiteSpace(statusPath) || !File.Exists(statusPath))
        {
            return 1;
        }

        string value = ReadSharedText(statusPath).Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int exitCode))
        {
            return exitCode is >= 0 and <= 255 ? exitCode : 1;
        }

        return value.ToUpperInvariant() switch
        {
            "HEALTHY" or "READY" or "TRUE" => 0,
            _ => 1,
        };
    }

    private static bool IsExecProbeArgument(string argument)
        => string.Equals(argument, "exec-probe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--exec-probe", StringComparison.OrdinalIgnoreCase);

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void CaptureEnvironment(string? capturePath)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string endpointName = Environment.GetEnvironmentVariable("TEST_ENDPOINT_NAME") ?? "http";
        string[] knownNames =
        {
            ResourceEnvironment.Application,
            ResourceEnvironment.Resource,
            ResourceEnvironment.Gateway,
            ResourceEnvironment.Environment,
            ResourceEnvironment.ContentRoot,
            ResourceEnvironment.Endpoint(endpointName, "HOST"),
            ResourceEnvironment.Endpoint(endpointName, "PORT"),
            ResourceEnvironment.Endpoint(endpointName, "SCHEME"),
            ResourceEnvironment.Endpoint(endpointName, "PUBLIC_URL"),
        };
        foreach (string name in knownNames)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                values[name] = value;
            }
        }

        string? requestedNames = Environment.GetEnvironmentVariable("TEST_CAPTURE_ENV_NAMES");
        if (!string.IsNullOrWhiteSpace(requestedNames))
        {
            foreach (string name in requestedNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (value is not null)
                {
                    values[name] = value;
                }
            }
        }

        WriteJson(capturePath, writer =>
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in values)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        });
    }

    private static void CaptureMounts(string? capturePath)
    {
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        var mounts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? requestedMounts = Environment.GetEnvironmentVariable("TEST_MOUNT_NAMES");
        if (!string.IsNullOrWhiteSpace(requestedMounts))
        {
            foreach (string mount in requestedMounts.Split(
                         ';',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string variable = ResourceEnvironment.Mount(mount);
                string? value = Environment.GetEnvironmentVariable(variable);
                if (value is not null)
                {
                    mounts[variable] = value;
                }
            }
        }

        WriteJson(capturePath, writer =>
        {
            writer.WriteStartObject();
            foreach ((string key, string path) in mounts)
            {
                writer.WritePropertyName(key);
                writer.WriteStartObject();
                writer.WriteString("path", path);

                if (File.Exists(path))
                {
                    writer.WriteString("kind", "file");
                    writer.WriteBoolean("exists", true);
                    writer.WriteBase64String("content", File.ReadAllBytes(path));
                }
                else if (Directory.Exists(path))
                {
                    writer.WriteString("kind", "directory");
                    writer.WriteBoolean("exists", true);
                }
                else
                {
                    writer.WriteString("kind", "missing");
                    writer.WriteBoolean("exists", false);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });
    }

    private static int IncrementLaunchCount(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 1;
        }

        EnsureParentDirectory(path);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = reader.ReadToEnd();
        int current = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        int next = checked(current + 1);

        stream.Position = 0;
        stream.SetLength(0);
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        {
            writer.Write(next.ToString(CultureInfo.InvariantCulture));
            writer.Flush();
        }

        stream.Flush(flushToDisk: true);
        return next;
    }

    private static void AppendRequest(string method, string target)
    {
        string? path = Environment.GetEnvironmentVariable("TEST_REQUEST_LOG_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        EnsureParentDirectory(path);
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine($"{method} {target}");
    }

    private static void WriteOptionalText(string? path, string value)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            WriteAtomically(path, Encoding.UTF8.GetBytes(value));
        }
    }

    private static void StartDescendant(string? processIdPath)
    {
        if (string.IsNullOrWhiteSpace(processIdPath))
        {
            return;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test host process path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("wait");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The test-host descendant did not start.");
        WriteOptionalText(
            processIdPath,
            process.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
        }

        WriteAtomically(path, buffer.ToArray());
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        EnsureParentDirectory(path);
        string fullPath = Path.GetFullPath(path);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return;
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };

        FileSystemEventHandler changed = (_, _) =>
        {
            if (File.Exists(fullPath))
            {
                completion.TrySetResult();
            }
        };
        RenamedEventHandler renamed = (_, _) =>
        {
            if (File.Exists(fullPath))
            {
                completion.TrySetResult();
            }
        };

        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Renamed += renamed;

        if (File.Exists(fullPath))
        {
            completion.TrySetResult();
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureParentDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required environment variable '{name}' was not supplied.");

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException($"Endpoint port '{value}' is not between 1 and 65535.");
        }

        return port;
    }

    private static async Task<IPAddress> ResolveAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        foreach (IPAddress candidate in addresses)
        {
            if (candidate.AddressFamily is AddressFamily.InterNetwork)
            {
                return candidate;
            }
        }

        if (addresses.Length == 0)
        {
            throw new InvalidOperationException($"Endpoint host '{host}' did not resolve to an address.");
        }

        return addresses[0];
    }

    private static string ReasonPhrase(int statusCode)
        => statusCode switch
        {
            200 => "OK",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Status",
        };

    private sealed class StopSignalSubscription : IDisposable
    {
        private readonly CancellationTokenSource _stopping;
        private readonly bool _ignore;
        private readonly string? _observedPath;
        private readonly EventWaitHandle? _stopEvent;
        private readonly RegisteredWaitHandle? _registeredWait;
        private readonly PosixSignalRegistration? _terminate;
        private int _observed;

        public StopSignalSubscription(CancellationTokenSource stopping)
        {
            _stopping = stopping;
            _ignore = string.Equals(
                Environment.GetEnvironmentVariable("TEST_IGNORE_STOP"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            _observedPath = Environment.GetEnvironmentVariable("TEST_STOP_OBSERVED_PATH");

            string? stopEventName = Environment.GetEnvironmentVariable(ResourceEnvironment.StopEvent);
            if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(stopEventName))
            {
                _stopEvent = EventWaitHandle.OpenExisting(stopEventName);
                _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    _stopEvent,
                    static (state, _) => ((StopSignalSubscription)state!).Observe(),
                    this,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
            }
            else if (!OperatingSystem.IsWindows())
            {
                _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    Observe();
                });
            }
        }

        public void Observe()
        {
            if (Interlocked.Exchange(ref _observed, 1) == 0)
            {
                WriteOptionalText(_observedPath, "observed");
            }

            if (!_ignore)
            {
                _stopping.Cancel();
            }
        }

        public void Dispose()
        {
            _registeredWait?.Unregister(null);
            _stopEvent?.Dispose();
            _terminate?.Dispose();
        }
    }
}
