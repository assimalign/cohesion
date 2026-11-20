using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// One value of the <c>forward-proxy</c> setting: its rules, validated and compiled. A table never
/// changes; the setting replaces it whole.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("Count = {Rules.Count}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ForwardProxyRuleTable
{
    private readonly ForwardProxyRuleEntry[] _entries;

    private ForwardProxyRuleTable(ForwardProxyRuleEntry[] entries)
    {
        _entries = entries;

        ForwardProxyRule[] rules = new ForwardProxyRule[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            rules[i] = entries[i].Rule;
        }

        Rules = Array.AsReadOnly(rules);
    }

    /// <summary>
    /// Gets the rules, with copies of the lists they were given.
    /// </summary>
    public IReadOnlyList<ForwardProxyRule> Rules { get; }

    /// <summary>
    /// Creates a table from rules the options already validated.
    /// </summary>
    public static ForwardProxyRuleTable Create(IReadOnlyList<ForwardProxyRuleEntry> entries)
    {
        ForwardProxyRuleEntry[] copy = new ForwardProxyRuleEntry[entries.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = entries[i];
        }

        return new ForwardProxyRuleTable(copy);
    }

    /// <summary>
    /// Validates and compiles a whole value of the setting.
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <param name="paramName">The parameter the rules came from, for the exception.</param>
    /// <exception cref="ArgumentException">A rule is <see langword="null"/> or not valid.</exception>
    public static ForwardProxyRuleTable Create(IReadOnlyList<ForwardProxyRule> rules, string paramName)
    {
        ForwardProxyRuleEntry[] entries = new ForwardProxyRuleEntry[rules.Count];

        for (int i = 0; i < entries.Length; i++)
        {
            ForwardProxyRule? rule = rules[i];
            if (rule is null)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.RuleNull, i), paramName);
            }

            entries[i] = ForwardProxyRuleEntry.Create(rule, i, paramName);
        }

        return new ForwardProxyRuleTable(entries);
    }

    /// <summary>
    /// Decides what the rules allow for a target: <see cref="ForwardProxyAccess.Open"/> when a rule
    /// matches it through an exact pattern, <see cref="ForwardProxyAccess.Restricted"/> when rules
    /// match it only through wildcard patterns, and <see cref="ForwardProxyAccess.Denied"/> otherwise.
    /// </summary>
    /// <param name="host">The target's host; its port is ignored.</param>
    /// <param name="port">The target's port, explicit or the scheme's default.</param>
    /// <param name="defaultPort">The port a rule with no ports allows: 80 for <c>http</c>, 443 otherwise.</param>
    public ForwardProxyAccess Evaluate(HttpHost host, int port, int defaultPort)
    {
        ForwardProxyAccess access = ForwardProxyAccess.Denied;

        foreach (ForwardProxyRuleEntry entry in _entries)
        {
            ForwardProxyAccess allowed = entry.Evaluate(host, port, defaultPort);
            if (allowed == ForwardProxyAccess.Open)
            {
                return ForwardProxyAccess.Open;
            }

            if (allowed > access)
            {
                access = allowed;
            }
        }

        return access;
    }

    private sealed class DebugView
    {
        private readonly ForwardProxyRuleTable _target;

        public DebugView(ForwardProxyRuleTable value)
        {
            _target = value;
        }

        public IReadOnlyList<ForwardProxyRule> Rules => _target.Rules;
    }
}
