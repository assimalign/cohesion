using System;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class GatewayCommandLineOptions
{
    public GatewayRunMode RunMode { get; private set; } = GatewayRunMode.Run;

    public string? Gateway { get; private set; }

    public string? Environment { get; private set; }

    public bool Adopt { get; private set; }

    public bool RestartOrphans { get; private set; }

    public static GatewayCommandLineOptions Parse(string[]? args)
    {
        var options = new GatewayCommandLineOptions();

        if (args is null)
        {
            return options;
        }

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index] ?? string.Empty;

            if (TrySplit(argument, "--mode", out string? inlineValue))
            {
                string value = ReadRequiredValue(args, ref index, "--mode", inlineValue);
                options.RunMode = ParseRunMode(value);
            }
            else if (TrySplit(argument, "--gateway", out inlineValue))
            {
                options.Gateway = ReadRequiredValue(args, ref index, "--gateway", inlineValue);
            }
            else if (TrySplit(argument, "--environment", out inlineValue))
            {
                options.Environment = ReadRequiredValue(args, ref index, "--environment", inlineValue);
            }
            else if (TrySplit(argument, "--adopt", out inlineValue))
            {
                options.Adopt = ReadOptionalBoolean(args, ref index, "--adopt", inlineValue);
            }
            else if (TrySplit(argument, "--restart-orphans", out inlineValue))
            {
                options.RestartOrphans = ReadOptionalBoolean(
                    args,
                    ref index,
                    "--restart-orphans",
                    inlineValue);
            }
        }

        return options;
    }

    private static bool TrySplit(string argument, string option, out string? inlineValue)
    {
        if (string.Equals(argument, option, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = null;
            return true;
        }

        if (argument.Length > option.Length &&
            argument[option.Length] == '=' &&
            argument.AsSpan(0, option.Length).Equals(option, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = argument[(option.Length + 1)..];
            return true;
        }

        inlineValue = null;
        return false;
    }

    private static string ReadRequiredValue(
        string[] args,
        ref int index,
        string option,
        string? inlineValue)
    {
        string? value = inlineValue;
        if (value is null)
        {
            if (index + 1 >= args.Length || IsOption(args[index + 1]))
            {
                throw new ArgumentException($"Option '{option}' requires a value.", nameof(args));
            }

            value = args[++index];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Option '{option}' requires a non-empty value.", nameof(args));
        }

        return value;
    }

    private static bool ReadOptionalBoolean(
        string[] args,
        ref int index,
        string option,
        string? inlineValue)
    {
        if (inlineValue is not null)
        {
            return ParseBoolean(option, inlineValue);
        }

        if (index + 1 < args.Length && bool.TryParse(args[index + 1], out bool parsed))
        {
            index++;
            return parsed;
        }

        return true;
    }

    private static GatewayRunMode ParseRunMode(string value)
    {
        if (string.Equals(value, "run", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.Run;
        }

        if (string.Equals(value, "apply", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.Apply;
        }

        if (string.Equals(value, "teardown", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.Teardown;
        }

        if (string.Equals(value, "bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.Bootstrap;
        }

        if (string.Equals(value, "describe", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.Describe;
        }

        if (string.Equals(value, "render", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.Render;
        }

        throw new ArgumentException(
            $"Unknown gateway run mode '{value}'. Expected run, apply, teardown, bootstrap, describe, or render.",
            nameof(value));
    }

    private static bool ParseBoolean(string option, string value)
    {
        if (bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"Option '{option}' expects 'true' or 'false'.", nameof(value));
    }

    private static bool IsOption(string argument) => argument.StartsWith("--", StringComparison.Ordinal);
}
