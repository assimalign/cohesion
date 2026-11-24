using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// A validated forward-proxy rule compiled for matching: one matcher for its exact patterns, one for
/// its wildcard patterns, and its ports.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ForwardProxyRuleEntry")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ForwardProxyRuleEntry
{
    private readonly HttpHostMatcher? _exact;
    private readonly HttpHostMatcher? _wildcard;
    private readonly int[] _ports;

    private ForwardProxyRuleEntry(ForwardProxyRule rule, HttpHostMatcher? exact, HttpHostMatcher? wildcard, int[] ports)
    {
        Rule = rule;
        _exact = exact;
        _wildcard = wildcard;
        _ports = ports;
    }

    /// <summary>
    /// Gets the rule, with copies of the lists it was given.
    /// </summary>
    public ForwardProxyRule Rule { get; }

    /// <summary>
    /// Validates and compiles a rule.
    /// </summary>
    /// <param name="rule">The rule, not <see langword="null"/>.</param>
    /// <param name="index">The rule's position, which names it in an exception.</param>
    /// <param name="paramName">The parameter the rule came from, for the exception.</param>
    /// <exception cref="ArgumentException">The rule is not valid.</exception>
    public static ForwardProxyRuleEntry Create(ForwardProxyRule rule, int index, string paramName)
    {
        IReadOnlyList<string>? hosts = rule.Hosts;
        if (hosts is null || hosts.Count == 0)
        {
            throw new ArgumentException(Format(ErrorMessages.RuleHostsMissing, index), paramName);
        }

        List<string> exact = new();
        List<string> wildcard = new();
        string[] hostsCopy = new string[hosts.Count];

        for (int i = 0; i < hostsCopy.Length; i++)
        {
            string pattern = hosts[i];
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException(Format(ErrorMessages.RuleHostPatternEmpty, index), paramName);
            }

            string trimmed = pattern.Trim();
            (trimmed == "*" || trimmed.StartsWith("*.", StringComparison.Ordinal) ? wildcard : exact).Add(trimmed);
            hostsCopy[i] = pattern;
        }

        HttpHostMatcher? exactMatcher;
        HttpHostMatcher? wildcardMatcher;
        try
        {
            // HttpHostMatcher validates each pattern: a port, a malformed host or a misplaced '*' throws.
            exactMatcher = exact.Count == 0 ? null : HttpHostMatcher.Create(exact);
            wildcardMatcher = wildcard.Count == 0 ? null : HttpHostMatcher.Create(wildcard);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(Format(ErrorMessages.RuleHostPatternInvalid, index, exception.Message), paramName, exception);
        }

        IReadOnlyList<int>? ports = rule.Ports;
        if (ports is null)
        {
            throw new ArgumentException(Format(ErrorMessages.RulePortsNull, index), paramName);
        }

        int[] portsCopy = new int[ports.Count];
        for (int i = 0; i < portsCopy.Length; i++)
        {
            int port = ports[i];
            if (port is < 1 or > 65535)
            {
                throw new ArgumentException(Format(ErrorMessages.RulePortInvalid, index, port), paramName);
            }

            portsCopy[i] = port;
        }

        ForwardProxyRule snapshot = rule with
        {
            Hosts = Array.AsReadOnly(hostsCopy),
            Ports = Array.AsReadOnly(portsCopy),
        };

        return new ForwardProxyRuleEntry(snapshot, exactMatcher, wildcardMatcher, portsCopy);
    }

    /// <summary>
    /// Decides what the rule allows for a target.
    /// </summary>
    /// <param name="host">The target's host; its port is ignored.</param>
    /// <param name="port">The target's port, explicit or the scheme's default.</param>
    /// <param name="defaultPort">The port a rule with no ports allows: 80 for <c>http</c>, 443 otherwise.</param>
    public ForwardProxyAccess Evaluate(HttpHost host, int port, int defaultPort)
    {
        bool portAllowed = _ports.Length == 0 ? port == defaultPort : Array.IndexOf(_ports, port) >= 0;
        if (!portAllowed)
        {
            return ForwardProxyAccess.Denied;
        }

        if (_exact is not null && _exact.IsMatch(host))
        {
            return ForwardProxyAccess.Open;
        }

        return _wildcard is not null && _wildcard.IsMatch(host) ? ForwardProxyAccess.Restricted : ForwardProxyAccess.Denied;
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ForwardProxyRuleEntry _target;

        public DebugView(ForwardProxyRuleEntry value)
        {
            _target = value;
        }

        public ForwardProxyRule Rule => _target.Rule;
    }
}
