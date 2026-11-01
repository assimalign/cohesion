using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Assimalign.Cohesion;

/// <summary>
/// Reads the Cohesion runtime contract from the environment: the application environment name, raw
/// variable values, and the typed endpoint, dependency, and mount readers over the names that
/// <see cref="Variables"/> defines.
/// </summary>
/// <remarks>
/// The environment is the process's, unless the current flow runs in an
/// <see cref="AppEnvironmentScope"/> that a host of programs entered for it
/// (<see cref="Enter"/>): then every reader without an explicit dictionary reads the scope's
/// variables first.
/// </remarks>
public static partial class AppEnvironment
{
    private static readonly AsyncLocal<AppEnvironmentScope?> _current = new();
    private static readonly PosixSignal[] _shutdownSignals = [PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM];

    /// <summary>
    /// Gets the scope the current flow runs in, or <see langword="null"/> when it reads the
    /// process's environment.
    /// </summary>
    public static AppEnvironmentScope? Current => _current.Value;

    /// <summary>
    /// Runs the current flow, and the work it starts from here, in a scope until the returned
    /// handle is disposed.
    /// </summary>
    /// <remarks>
    /// Enter the scope on the flow that runs the program, such as at the start of the thread the
    /// program's entry point runs on. Work the flow starts, such as tasks and timers that capture
    /// the flow's execution context, keeps the scope after the handle is disposed.
    /// </remarks>
    /// <param name="scope">The scope.</param>
    /// <returns>A handle that restores the flow's previous scope when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public static IDisposable Enter(AppEnvironmentScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        AppEnvironmentScope? previous = _current.Value;
        _current.Value = scope;
        return new ScopeExit(previous);
    }

    /// <summary>
    /// Registers a request for the shutdown of the program the current flow runs: on the process's
    /// shutdown signals, or, inside an <see cref="AppEnvironmentScope"/>, on the scope's
    /// <see cref="AppEnvironmentScope.Shutdown"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SIGINT and SIGQUIT are Ctrl+C and its relatives at a console, and SIGTERM is how a platform such
    /// as Kubernetes or Docker stops a container. Left alone, each ends the process at once and cuts
    /// off its work. A registration cancels each signal's default action and calls
    /// <paramref name="requestShutdown"/> instead, once per signal received, so the program drains and
    /// the process exits when its entry point returns. A platform that delivers none of a signal, such
    /// as a browser, skips it.
    /// </para>
    /// <para>
    /// A program inside a scope shares its process with other programs, so the process's signals are
    /// not its own. It registers on the scope's shutdown instead, which the host of the programs
    /// signals, and registers no process signal.
    /// </para>
    /// </remarks>
    /// <param name="requestShutdown">Requests the program's shutdown. It must not throw: a signal handler that throws ends the process.</param>
    /// <returns>The registration, which removes the handlers when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requestShutdown"/> is <see langword="null"/>.</exception>
    public static IDisposable RegisterShutdown(Action requestShutdown)
    {
        ArgumentNullException.ThrowIfNull(requestShutdown);

        if (_current.Value is { } scope)
        {
            return scope.Shutdown.Register(requestShutdown);
        }

        var registrations = new List<PosixSignalRegistration>(_shutdownSignals.Length);
        foreach (PosixSignal signal in _shutdownSignals)
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    requestShutdown();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // The platform delivers no such signal.
            }
        }

        return new SignalRegistration(registrations);
    }

    /// <summary>
    /// Gets every variable the current flow reads.
    /// </summary>
    /// <remarks>
    /// Outside a scope these are the process's variables. Inside one they are the scope's variables
    /// and the process's variables outside the runtime contract.
    /// </remarks>
    /// <returns>A snapshot of the variables, by name.</returns>
    public static IDictionary<string, string?> GetValues()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        AppEnvironmentScope? scope = _current.Value;
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && (scope is null || !IsContractVariable(name)))
            {
                values[name] = entry.Value as string;
            }
        }

        if (scope is not null)
        {
            foreach (KeyValuePair<string, string> variable in scope.Variables)
            {
                values[variable.Key] = variable.Value;
            }
        }

        return values;
    }

    /// <summary>
    /// Gets the environment name for the current process.
    /// </summary>
    /// <returns>
    /// The Cohesion environment override, the .NET environment fallback, or
    /// <see cref="Keys.DefaultEnvironmentName"/> when neither variable is set.
    /// </returns>
    public static string GetEnvironmentName()
    {
        return GetEnvironmentNameCore(environment: null);
    }

    /// <summary>
    /// Gets the environment name from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <returns>
    /// The Cohesion environment override, the .NET environment fallback, or
    /// <see cref="Keys.DefaultEnvironmentName"/> when neither variable is present.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    public static string GetEnvironmentName(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return GetEnvironmentNameCore(environment);
    }

    /// <summary>
    /// Gets a variable from the current flow's environment: its scope's, then the process's.
    /// </summary>
    /// <remarks>
    /// Inside a scope, a variable of the runtime contract that the scope does not set reads as
    /// unset; see <see cref="AppEnvironmentScope"/>.
    /// </remarks>
    /// <param name="variable">The variable name.</param>
    /// <returns>The variable value, or <see langword="null"/> when it is not set.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static string? GetValue(string variable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);

        if (_current.Value is { } scope)
        {
            if (scope.Variables.TryGetValue(variable, out string? value))
            {
                return value;
            }

            if (IsContractVariable(variable))
            {
                return null;
            }
        }

        return System.Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process);
    }

    /// <summary>
    /// Gets a variable from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="variable">The variable name.</param>
    /// <returns>The variable value, or <see langword="null"/> when it is not present.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static string? GetValue(IDictionary<string, string?> environment, string variable)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);

        return environment.TryGetValue(variable, out string? value) ? value : null;
    }

    /// <summary>
    /// Attempts to read a port from the current process environment.
    /// </summary>
    /// <param name="variable">The port variable name.</param>
    /// <param name="port">The parsed port when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a valid port was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetPort(string variable, out int port)
    {
        return TryParsePort(GetValue(variable), out port);
    }

    /// <summary>
    /// Attempts to read a port from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="variable">The port variable name.</param>
    /// <param name="port">The parsed port when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a valid port was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetPort(IDictionary<string, string?> environment, string variable, out int port)
    {
        return TryParsePort(GetValue(environment, variable), out port);
    }

    /// <summary>
    /// Attempts to read an absolute URI from the current process environment.
    /// </summary>
    /// <param name="variable">The URI variable name.</param>
    /// <param name="uri">The parsed URI when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an absolute URI was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetUri(string variable, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(GetValue(variable), UriKind.Absolute, out uri);
    }

    /// <summary>
    /// Attempts to read an absolute URI from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="variable">The URI variable name.</param>
    /// <param name="uri">The parsed URI when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an absolute URI was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetUri(
        IDictionary<string, string?> environment,
        string variable,
        [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(GetValue(environment, variable), UriKind.Absolute, out uri);
    }

    /// <summary>
    /// Attempts to read a declared endpoint from the current process environment.
    /// </summary>
    /// <param name="endpoint">The declared endpoint name.</param>
    /// <param name="address">The endpoint address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a complete endpoint address was read; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method reads the bind host, port, and scheme. Read the separately advertised public
    /// URL with <see cref="TryGetUri"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is empty.</exception>
    public static bool TryGetEndpoint(string endpoint, [NotNullWhen(true)] out Uri? address)
    {
        return TryGetEndpointCore(environment: null, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a declared endpoint from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="endpoint">The declared endpoint name.</param>
    /// <param name="address">The endpoint address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a complete endpoint address was read; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method reads the bind host, port, and scheme. Read the separately advertised public
    /// URL with <see cref="TryGetUri"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is empty.</exception>
    public static bool TryGetEndpoint(
        IDictionary<string, string?> environment,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return TryGetEndpointCore(environment, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a dependency endpoint from the current process environment.
    /// </summary>
    /// <param name="resource">The dependency resource name.</param>
    /// <param name="endpoint">The dependency endpoint name.</param>
    /// <param name="address">The dependency address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a dependency address was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="resource"/> or <paramref name="endpoint"/> is empty.
    /// </exception>
    public static bool TryGetDependency(
        string resource,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        return TryGetDependencyCore(environment: null, resource, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a dependency endpoint from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="resource">The dependency resource name.</param>
    /// <param name="endpoint">The dependency endpoint name.</param>
    /// <param name="address">The dependency address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a dependency address was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="resource"/> or <paramref name="endpoint"/> is empty.
    /// </exception>
    public static bool TryGetDependency(
        IDictionary<string, string?> environment,
        string resource,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return TryGetDependencyCore(environment, resource, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a mount path from the current process environment.
    /// </summary>
    /// <param name="mount">The declared mount name.</param>
    /// <param name="path">The mount path when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a non-empty mount path was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mount"/> is empty.</exception>
    public static bool TryGetMount(string mount, [NotNullWhen(true)] out string? path)
    {
        path = GetValue(Variables.Mount(mount));
        return !string.IsNullOrWhiteSpace(path);
    }

    /// <summary>
    /// Attempts to read a mount path from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="mount">The declared mount name.</param>
    /// <param name="path">The mount path when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a non-empty mount path was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mount"/> is empty.</exception>
    public static bool TryGetMount(
        IDictionary<string, string?> environment,
        string mount,
        [NotNullWhen(true)] out string? path)
    {
        path = GetValue(environment, Variables.Mount(mount));
        return !string.IsNullOrWhiteSpace(path);
    }

    private static string GetEnvironmentNameCore(IDictionary<string, string?>? environment)
    {
        string? cohesionEnvironment = ReadValue(environment, Keys.EnvironmentKey);
        string? dotNetEnvironment = ReadValue(environment, Keys.DotNetEnvironmentKey);

        return cohesionEnvironment ?? dotNetEnvironment ?? Keys.DefaultEnvironmentName;
    }

    private static bool TryGetEndpointCore(
        IDictionary<string, string?>? environment,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        string hostVariable = Variables.Endpoint(endpoint, "HOST");
        string portVariable = Variables.Endpoint(endpoint, "PORT");
        string schemeVariable = Variables.Endpoint(endpoint, "SCHEME");
        string? host = ReadValue(environment, hostVariable);
        string? port = ReadValue(environment, portVariable);
        string? scheme = ReadValue(environment, schemeVariable);

        return TryCreateAddress(scheme, host, port, out address);
    }

    private static bool TryGetDependencyCore(
        IDictionary<string, string?>? environment,
        string resource,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        string? url = ReadValue(environment, Variables.Dependency(resource, endpoint, "URL"));
        if (!string.IsNullOrWhiteSpace(url))
        {
            return Uri.TryParseEndpoint(url, out address);
        }

        return TryCreateAddress(
            ReadValue(environment, Variables.Dependency(resource, endpoint, "SCHEME")),
            ReadValue(environment, Variables.Dependency(resource, endpoint, "HOST")),
            ReadValue(environment, Variables.Dependency(resource, endpoint, "PORT")),
            out address);
    }

    private static bool TryCreateAddress(
        string? scheme,
        string? host,
        string? portValue,
        [NotNullWhen(true)] out Uri? address)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(scheme)
            || string.IsNullOrWhiteSpace(host)
            || !TryParsePort(portValue, out int port))
        {
            return false;
        }

        return Uri.TryCreateEndpoint(scheme, host, port, path: null, out address);
    }

    private static string? ReadValue(IDictionary<string, string?>? environment, string variable)
    {
        return environment is null ? GetValue(variable) : GetValue(environment, variable);
    }

    private static bool TryParsePort(string? value, out int port)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            port = default;
            return false;
        }

        return true;
    }

    // Environment variable names are case-insensitive on Windows, so a process variable hides as a
    // contract variable whatever its case.
    private static bool IsContractVariable(string variable)
    {
        return variable.StartsWith(Variables.Prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScopeExit : IDisposable
    {
        private readonly AppEnvironmentScope? _previous;
        private bool _isDisposed;

        public ScopeExit(AppEnvironmentScope? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _current.Value = _previous;
        }
    }

    private sealed class SignalRegistration : IDisposable
    {
        private readonly List<PosixSignalRegistration> _registrations;

        public SignalRegistration(List<PosixSignalRegistration> registrations)
        {
            _registrations = registrations;
        }

        public void Dispose()
        {
            foreach (PosixSignalRegistration registration in _registrations)
            {
                registration.Dispose();
            }
        }
    }
}
