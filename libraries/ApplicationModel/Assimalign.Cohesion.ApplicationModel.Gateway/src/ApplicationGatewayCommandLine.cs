using System;
using System.ComponentModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Applies gateway-runtime command-line values that are intentionally outside the base
/// application-model parser.
/// </summary>
/// <remarks>
/// This is an SDK-generated-code seam. Application authors normally pass <c>args</c> to the
/// generated <c>UseGateway(args)</c> verb instead of calling this type directly.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ApplicationGatewayCommandLine
{
    /// <summary>Applies repeatable <c>--parameter name=value</c> overrides.</summary>
    /// <param name="options">The selected gateway's common options.</param>
    /// <param name="args">The original gateway arguments.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="args"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A parameter binding is missing or malformed.</exception>
    public static void Apply(ApplicationGatewayOptions options, string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index] ?? string.Empty;
            string? binding = null;
            if (string.Equals(argument, "--parameter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Option '--parameter' requires a name=value binding.", nameof(args));
                }

                binding = args[++index];
            }
            else if (argument.StartsWith("--parameter=", StringComparison.OrdinalIgnoreCase))
            {
                binding = argument["--parameter=".Length..];
            }

            if (binding is null)
            {
                continue;
            }

            int separator = binding.IndexOf('=');
            if (separator <= 0)
            {
                throw new ArgumentException(
                    $"Parameter binding '{binding}' must use the form name=value.",
                    nameof(args));
            }

            string name = binding[..separator];
            string value = binding[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A parameter name must not be empty.", nameof(args));
            }

            options.Parameters[name] = value;
        }
    }
}
