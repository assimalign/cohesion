using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Configuration.CommandLine;

/// <summary>
/// Loads configuration values from command-line arguments.
/// </summary>
public class ConfigurationCommandLineProvider : ConfigurationProvider
{
    private readonly string[] _args;
    private readonly Dictionary<string, string>? _switchMappings;

    /// <summary>
    /// Creates a command-line provider with the specified arguments and optional switch mappings.
    /// </summary>
    /// <param name="args">The command-line arguments to parse.</param>
    /// <param name="switchMappings">Optional switch mappings for short and aliased switches.</param>
    public ConfigurationCommandLineProvider(
        IEnumerable<string> args,
        IDictionary<string, string>? switchMappings = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        _args = [.. args];
        _switchMappings = switchMappings is null
            ? null
            : GetValidatedSwitchMappingsCopy(switchMappings);
    }

    /// <inheritdoc />
    public override string Name => nameof(ConfigurationCommandLineProvider);

    /// <inheritdoc />
    protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
    {
        Parse(_args, entries, _switchMappings, cancellationToken);
        return Task.CompletedTask;
    }

    internal static void Parse(
        ReadOnlySpan<string> args,
        IDictionary<Path, string?> entries,
        IDictionary<string, string>? switchMappings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int index = 0;

        while (index < args.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string currentArg = args[index];

            if (string.IsNullOrWhiteSpace(currentArg))
            {
                index++;
                continue;
            }

            int keyStartIndex = 0;

            if (currentArg.StartsWith("--", StringComparison.Ordinal))
            {
                keyStartIndex = 2;
            }
            else if (currentArg.StartsWith("-", StringComparison.Ordinal))
            {
                keyStartIndex = 1;
            }
            else if (currentArg.StartsWith("/", StringComparison.Ordinal))
            {
                currentArg = $"--{currentArg[1..]}";
                keyStartIndex = 2;
            }

            int separator = currentArg.IndexOf('=');

            if (separator < 0)
            {
                if (keyStartIndex == 0)
                {
                    index++;
                    continue;
                }

                if (!TryResolveKey(currentArg, keyStartIndex, switchMappings, out string? key, out bool isMappedShortSwitch))
                {
                    index++;
                    continue;
                }

                if (keyStartIndex == 1 && !isMappedShortSwitch)
                {
                    index++;
                    continue;
                }

                index++;

                if (index >= args.Length)
                {
                    continue;
                }

                entries[Path.Parse(key)] = args[index];
                index++;
                continue;
            }

            string keySegment = currentArg[..separator];

            if (!TryResolveKey(keySegment, keyStartIndex, switchMappings, out string? mappedKey, out bool mappedShortKey))
            {
                if (keyStartIndex == 1)
                {
                    throw new FormatException($"Short switch '{keySegment}' is not defined in the switch mappings.");
                }

                index++;
                continue;
            }

            if (keyStartIndex == 1 && !mappedShortKey)
            {
                throw new FormatException($"Short switch '{keySegment}' is not defined in the switch mappings.");
            }

            entries[Path.Parse(mappedKey)] = currentArg[(separator + 1)..];
            index++;
        }
    }

    private static bool TryResolveKey(
        string keySegment,
        int keyStartIndex,
        IDictionary<string, string>? switchMappings,
        out string key,
        out bool isMappedShortSwitch)
    {
        isMappedShortSwitch = false;

        if (switchMappings is not null && switchMappings.TryGetValue(keySegment, out string? mappedKey))
        {
            key = mappedKey;
            isMappedShortSwitch = keySegment.StartsWith("-", StringComparison.Ordinal)
                && !keySegment.StartsWith("--", StringComparison.Ordinal);
            return true;
        }

        if (keyStartIndex == 0)
        {
            key = keySegment;
            return true;
        }

        if (keyStartIndex == 1)
        {
            key = string.Empty;
            return false;
        }

        key = currentArgKey(keySegment, keyStartIndex);
        return !string.IsNullOrEmpty(key);

        static string currentArgKey(string candidate, int startIndex)
        {
            return candidate.Length <= startIndex ? string.Empty : candidate[startIndex..];
        }
    }

    private static Dictionary<string, string> GetValidatedSwitchMappingsCopy(IDictionary<string, string> switchMappings)
    {
        ArgumentNullException.ThrowIfNull(switchMappings);

        var validatedMappings = new Dictionary<string, string>(switchMappings.Count, StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string value) in switchMappings)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Switch mapping keys cannot be null or whitespace.", nameof(switchMappings));
            }

            if (!key.StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Switch mapping '{key}' must start with '-' or '--'.",
                    nameof(switchMappings));
            }

            if (validatedMappings.ContainsKey(key))
            {
                throw new ArgumentException(
                    $"Switch mapping '{key}' is duplicated when compared case-insensitively.",
                    nameof(switchMappings));
            }

            validatedMappings.Add(key, value);
        }

        return validatedMappings;
    }
}
