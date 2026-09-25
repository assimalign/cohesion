using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal sealed class GatewayCommandLineOptions
{
    private readonly List<string> _externalBindings = new();
    private readonly List<ResourceName> _realize = new();
    private string? _developerName;
    private string? _peerName;
    private string? _exportSource;
    private readonly List<string> _allowedCommandKinds = new();

    public GatewayRunMode RunMode { get; private set; } = GatewayRunMode.Run;

    public string? Gateway { get; private set; }

    public string? Environment { get; private set; }

    public bool Adopt { get; private set; }

    public bool RestartOrphans { get; private set; }

    public IReadOnlyList<string> ExternalBindings =>
        new ReadOnlyCollection<string>(_externalBindings);

    public IReadOnlyList<ResourceName> Realize =>
        new ReadOnlyCollection<ResourceName>(_realize);

    public GatewayCommand? Command { get; private set; }

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
            else if (TrySplit(argument, "--external", out inlineValue))
            {
                options._externalBindings.Add(
                    ReadRequiredValue(args, ref index, "--external", inlineValue));
            }
            else if (TrySplit(argument, "--realize", out inlineValue))
            {
                options._realize.Add((ResourceName)ReadRequiredValue(
                    args,
                    ref index,
                    "--realize",
                    inlineValue));
            }
            else if (TrySplit(argument, "--developer", out inlineValue))
            {
                options._developerName = ReadRequiredValue(
                    args,
                    ref index,
                    "--developer",
                    inlineValue);
            }
            else if (TrySplit(argument, "--peer", out inlineValue))
            {
                options._peerName = ReadRequiredValue(args, ref index, "--peer", inlineValue);
            }
            else if (TrySplit(argument, "--from", out inlineValue))
            {
                options._exportSource = ReadRequiredValue(args, ref index, "--from", inlineValue);
            }
            else if (TrySplit(argument, "--allow", out inlineValue))
            {
                string value = ReadRequiredValue(args, ref index, "--allow", inlineValue);
                foreach (string kind in value.Split(',', StringSplitOptions.TrimEntries))
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(kind, "--allow");
                    options._allowedCommandKinds.Add(kind);
                }
            }
        }

        options.ValidateCommand();
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

        if (string.Equals(value, "trust-issue", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.TrustIssue;
        }

        if (string.Equals(value, "trust-add", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayRunMode.TrustAdd;
        }

        throw new ArgumentException(
            $"Unknown gateway run mode '{value}'. Expected run, apply, teardown, bootstrap, " +
            "describe, render, trust-issue, or trust-add.",
            nameof(value));
    }

    private void ValidateCommand()
    {
        if (RunMode == GatewayRunMode.TrustIssue)
        {
            if (_peerName is not null || _exportSource is not null || _allowedCommandKinds.Count > 0)
            {
                throw new ArgumentException(
                    "Options '--peer', '--from', and '--allow' are not valid with --mode trust-issue.");
            }

            Command = new GatewayCommand(RunMode, developerName: _developerName);
            return;
        }

        if (RunMode == GatewayRunMode.TrustAdd)
        {
            if (_developerName is not null)
            {
                throw new ArgumentException(
                    "Option '--developer' is not valid with --mode trust-add.");
            }

            Command = new GatewayCommand(
                RunMode,
                peerName: _peerName,
                exportSource: _exportSource,
                allowedCommandKinds: _allowedCommandKinds);
            return;
        }

        if (_developerName is not null || _peerName is not null || _exportSource is not null || _allowedCommandKinds.Count > 0)
        {
            throw new ArgumentException(
                "Options '--developer', '--peer', '--from', and '--allow' require a matching trust mode; '--allow' requires trust-add.");
        }
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
