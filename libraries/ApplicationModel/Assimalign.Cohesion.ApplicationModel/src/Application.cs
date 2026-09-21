using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The entry point for composing an application model. Mirrors the
/// <c>WebApplication.CreateBuilder()</c> idiom.
/// </summary>
public static class Application
{
    /// <summary>
    /// Creates an application set that resolves its member gateways through their control planes
    /// and reconciles them through one multi-model gateway instance.
    /// </summary>
    /// <param name="gateway">The shared gateway instance.</param>
    /// <param name="args">The root gateway command-line arguments.</param>
    /// <returns>An empty application set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gateway"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The gateway is not multi-model capable, or a command-line option is invalid or selects
    /// another gateway.
    /// </exception>
    /// <remarks>
    /// Apphosts use the Local environment when no environment option or process environment
    /// variable is supplied. Explicit environment values are preserved. Local and InProcess
    /// gateways also treat blank process environment values as Local.
    /// </remarks>
    public static IApplicationSet CreateSet(IApplicationGateway gateway, string[] args)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        GatewayCommandLineOptions options = GatewayCommandLineOptions.Parse(args);
        if (gateway is not IMultiModelApplicationGateway multiModelGateway)
        {
            throw new ArgumentException(
                $"Gateway '{gateway.Name}' does not implement {nameof(IMultiModelApplicationGateway)}.",
                nameof(gateway));
        }

        if (options.Gateway is not null &&
            !string.Equals(options.Gateway, gateway.Name.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Gateway '{options.Gateway}' was requested, but gateway '{gateway.Name}' was selected.",
                nameof(args));
        }

        IApplicationEnvironment environment = options.Environment is null
            ? ApplicationEnvironment.FromHost()
            : ApplicationEnvironment.FromName(options.Environment);
        if (options.Environment is null &&
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(AppEnvironment.Keys.EnvironmentKey)) &&
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(AppEnvironment.Keys.DotNetEnvironmentKey)) &&
            (string.Equals(gateway.Name.ToString(), "local", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(gateway.Name.ToString(), "inprocess", StringComparison.OrdinalIgnoreCase)))
        {
            environment = ApplicationEnvironment.FromName(AppEnvironment.Keys.Local);
        }

        return new CohesionApplicationSet(
            multiModelGateway,
            environment,
            options.RunMode,
            options.ExternalBindings,
            options.Realize);
    }

    /// <summary>
    /// Creates a new application builder using the legacy <c>application</c> identity.
    /// </summary>
    /// <remarks>
    /// Generated gateways use <see cref="CreateBuilder(ApplicationName, string[])"/> so their
    /// compiled identity is explicit. This overload remains for existing hand-authored callers.
    /// </remarks>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    public static IApplicationBuilder CreateBuilder() => new ApplicationBuilder();

    /// <summary>
    /// Creates a new application builder with the process command-line arguments.
    /// </summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    /// <exception cref="System.ArgumentException">
    /// A recognized gateway command-line option is missing or has an invalid value.
    /// </exception>
    /// <remarks>
    /// An unnamed builder derives a slug from the entry assembly only in Local. In
    /// other environments, call <see cref="IApplicationBuilder.UseName(ApplicationName)"/> before building.
    /// </remarks>
    public static IApplicationBuilder CreateBuilder(string[] args) => new ApplicationBuilder(args);

    /// <summary>
    /// Creates a new application builder with a compiled application identity and the
    /// process command-line arguments.
    /// </summary>
    /// <param name="name">The application identity compiled into the gateway.</param>
    /// <param name="args">The process command-line arguments.</param>
    /// <returns>A new <see cref="IApplicationBuilder"/>.</returns>
    /// <exception cref="System.ArgumentException">
    /// A recognized gateway command-line option is missing or has an invalid value.
    /// </exception>
    /// <remarks>
    /// The name is supplied directly to the builder; no assembly attribute is inspected at runtime.
    /// </remarks>
    public static IApplicationBuilder CreateBuilder(ApplicationName name, string[] args) =>
        new ApplicationBuilder(name, args);
}
