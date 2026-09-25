using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Cli.Internal;

internal sealed class Arguments
{
    private readonly List<string> _options;
    private readonly string[] _passthrough;

    internal Arguments(IEnumerable<string> args)
    {
        string[] values = args.ToArray();
        int boundary = Array.IndexOf(values, "--");
        _options = new List<string>(boundary < 0 ? values : values[..boundary]);
        _passthrough = boundary < 0 ? [] : values[(boundary + 1)..];
    }

    internal string? TakeValue(string option)
    {
        string? value = null;
        for (int index = 0; index < _options.Count; index++)
        {
            string argument = _options[index];
            if (argument != option && !argument.StartsWith(option + "=", StringComparison.Ordinal))
            {
                continue;
            }
            if (value is not null)
            {
                throw new CliException($"Option {option} may only be specified once.");
            }
            _options.RemoveAt(index);
            if (argument == option)
            {
                if (index == _options.Count || _options[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliException($"Option {option} requires a value.");
                }
                value = _options[index];
                _options.RemoveAt(index);
            }
            else
            {
                value = argument[(option.Length + 1)..];
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliException($"Option {option} requires a non-empty value.");
            }
            index--;
        }
        return value;
    }

    internal bool Has(string option) => _options.Any(value => value == option || value.StartsWith(option + "=", StringComparison.Ordinal));

    internal bool TakeFlag(string option)
    {
        if (_options.Any(value => value.StartsWith(option + "=", StringComparison.Ordinal)))
        {
            throw new CliException($"Option {option} does not take a value.");
        }
        return _options.RemoveAll(value => value == option) > 0;
    }

    internal string? TakeFirst(bool allowOptionLike = false)
    {
        if (_options.Count == 0 || (!allowOptionLike && _options[0].StartsWith("-", StringComparison.Ordinal)))
        {
            return null;
        }
        string value = _options[0];
        _options.RemoveAt(0);
        return value;
    }

    internal string[] Remaining => [.. _options, .. _passthrough];

    internal void RequireEmpty()
    {
        if (Remaining.Length != 0)
        {
            // Values may be credentials; do not echo unknown local-command arguments.
            throw new CliException("Unexpected arguments. Use cohesion --help for the command syntax.");
        }
    }
}
