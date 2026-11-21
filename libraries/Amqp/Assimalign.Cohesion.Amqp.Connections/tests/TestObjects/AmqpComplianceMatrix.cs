using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The compliance matrix (#431): every rule of every compliance map in one list, each with the Parts of AMQP 1.0
/// it cites and the tests or corpus cases that pin it, and the rules the engine does not hold yet.
/// </summary>
/// <remarks>
/// A map adds its rules here once, in <see cref="CreateEntries"/>; the checks and the per-Part report in
/// <see cref="AmqpComplianceMatrixTests"/> then cover them, as does the regression register's lookup of the rule
/// each defect broke.
/// </remarks>
internal static partial class AmqpComplianceMatrix
{
    /// <summary>
    /// Gets every rule of every compliance map.
    /// </summary>
    public static IReadOnlyList<AmqpComplianceEntry> Entries { get; } = CreateEntries();

    /// <summary>
    /// Gets the rules of AMQP 1.0 the engine does not hold yet, each with the work item that tracks it.
    /// </summary>
    public static IReadOnlyList<AmqpComplianceGap> Gaps { get; } =
    [
        new("G1", "A link attached elsewhere may be stolen: a second attach takes it over and the first is closed with amqp:link:stolen. The engine keeps no registry beyond the session and refuses a second attach of a name it holds on the session with amqp:not-allowed, so a host that lets a reconnecting client take over its link closes the old link with amqp:link:stolen itself.", "Part 2, section 2.6.1",
            WorkItem: "#1930"),
        new("G2", "An attach of a link the engine detached with an error must resume it with an unsettled map, and a pipelined re-attach without one ends the session with amqp:session:errant-link. The engine accepts it as a new attach.", "Part 2, sections 2.6.5 and 2.8.17",
            WorkItem: "#1931"),
        new("G3", "When one end closes a link while the other suspends it, the end that suspended reattaches the link and then closes it. The engine takes either crossing detach as the answer to its own: it does not reattach to close a link it suspended, and a peer that reattaches to close a link the engine closed reaches the application as a new link request.", "Part 2, section 2.6.6",
            WorkItem: "#1932"),
        new("G4", "A peer may open TLS in band, by sending the AMQP TLS header (protocol id 2) and starting the TLS handshake after it. The engine answers that header with the one it supports and closes (header-phase rule H3); TLS comes from the carrier instead, as the alternative establishment allows.", "Part 5, sections 5.2 and 5.2.1",
            WorkItem: "#1935"),
        new("G5", "A peer that sends past the incoming window the engine granted breaks the session with amqp:session:window-violation. The engine widens its window at half use as transfers arrive, so it never sees the window run out: what it buffers is bounded by link credit and a receiver's max-message-size, and max-message-size has no default.", "Part 2, sections 2.5.6 and 2.8.17",
            WorkItem: "#1934"),
        new("G6", "A delivery on a link detached without closing and without an error stays live, and a disposition that names it is applied. The engine fails a suspended link's deliveries when the link detaches and passes over a later disposition for them; resuming the link reconciles their state through the unsettled maps instead.", "Part 2, section 2.7.6",
            WorkItem: "#1933")
    ];

    /// <summary>
    /// Reads the Parts of the specification a reference cites, in the order it cites them.
    /// </summary>
    /// <param name="reference">
    /// One or more citations of the form <c>Part N, section X</c>, <c>Part N, sections X and Y</c> or
    /// <c>Part N, sections X to Y</c>, joined by <c>; </c> or <c>, and </c>.
    /// </param>
    /// <returns>
    /// The Parts cited, or <see langword="null"/> when a citation does not have that form, names a Part outside 1 to
    /// 5, or names a section outside the Part it cites.
    /// </returns>
    public static IReadOnlyList<int>? ReadParts(string reference)
    {
        List<int> parts = [];

        foreach (string citation in CitationSeparator().Split(reference))
        {
            Match match = Citation().Match(citation);

            if (!match.Success)
            {
                return null;
            }

            string part = match.Groups["part"].Value;

            foreach (Capture section in match.Groups["section"].Captures)
            {
                if (!section.Value.StartsWith(part + ".", StringComparison.Ordinal))
                {
                    return null;
                }
            }

            int number = part[0] - '0';

            if (!parts.Contains(number))
            {
                parts.Add(number);
            }
        }

        return parts;
    }

    /// <summary>
    /// Gets whether a map names corpus cases as its rules' evidence rather than tests.
    /// </summary>
    public static bool NamesCases(AmqpComplianceMap map)
    {
        return map is AmqpComplianceMap.HeaderPhase
            or AmqpComplianceMap.TypeSystem
            or AmqpComplianceMap.MessageFormat
            or AmqpComplianceMap.Terminus;
    }

    /// <summary>
    /// Gets whether a map holds the rule, or, for <see cref="AmqpComplianceMap.None"/>, whether no rule is named.
    /// </summary>
    public static bool HasRule(AmqpComplianceMap map, string? rule)
    {
        return map == AmqpComplianceMap.None
            ? rule is null
            : Entries.Any(entry => entry.Map == map && entry.Id == rule);
    }

    /// <summary>
    /// Gets whether a map's corpus holds a case with the name. A map that names tests holds no cases.
    /// </summary>
    public static bool HasCase(AmqpComplianceMap map, string name)
    {
        return map switch
        {
            AmqpComplianceMap.HeaderPhase => AmqpHeaderPhaseCorpus.Cases.Any(item => item.Name == name),
            AmqpComplianceMap.TypeSystem => AmqpTypeSystemCorpus.Cases.Any(item => item.Name == name),
            AmqpComplianceMap.MessageFormat => AmqpMessageFormatCorpus.Cases.Any(item => item.Name == name),
            AmqpComplianceMap.Terminus => AmqpTerminusCorpus.Cases.Any(item => item.Name == name),
            _ => false
        };
    }

    private static List<AmqpComplianceEntry> CreateEntries()
    {
        List<AmqpComplianceEntry> entries = [];

        foreach (AmqpHeaderPhaseRule rule in AmqpHeaderPhaseCorpus.Rules)
        {
            AmqpHeaderPhaseCase[] cases = [.. AmqpHeaderPhaseCorpus.Cases.Where(item => item.Rule == rule.Id)];

            entries.Add(new AmqpComplianceEntry(
                AmqpComplianceMap.HeaderPhase,
                rule.Id,
                rule.Statement,
                rule.Reference,
                rule.Forbids,
                rule.Leniency,
                [.. cases.Where(item => !item.Refuses).Select(item => item.Name)],
                [.. cases.Where(item => item.Refuses).Select(item => item.Name)]));
        }

        foreach (AmqpTypeDefinition type in AmqpTypeSystemCorpus.Types)
        {
            AmqpTypeEncodingCase[] cases = [.. AmqpTypeSystemCorpus.Cases.Where(item => item.Type == type.Name)];
            AmqpTypeEncodingCase[] refused = [.. cases.Where(IsRefusal)];

            entries.Add(new AmqpComplianceEntry(
                AmqpComplianceMap.TypeSystem,
                type.Name,
                $"The {type.Name} type decodes from and encodes to the constructors Part 1 defines for it, and only those.",
                type.Reference,
                refused.Length > 0,
                false,
                [.. cases.Where(item => !IsRefusal(item)).Select(item => item.Name)],
                [.. refused.Select(item => item.Name)]));
        }

        foreach (AmqpMessageFormatRule rule in AmqpMessageFormatCorpus.Rules)
        {
            AmqpMessageFormatCase[] cases = [.. AmqpMessageFormatCorpus.Cases.Where(item => item.Rule == rule.Id)];

            // Every message-format rule constrains what a message may hold, so each forbids what breaks it.
            entries.Add(new AmqpComplianceEntry(
                AmqpComplianceMap.MessageFormat,
                rule.Id,
                rule.Statement,
                rule.Reference,
                true,
                rule.Leniency,
                [.. cases.Where(item => item.Refusal is null).Select(item => item.Name)],
                [.. cases.Where(item => item.Refusal is not null).Select(item => item.Name)]));
        }

        foreach (AmqpTerminusRule rule in AmqpTerminusCorpus.Rules)
        {
            AmqpTerminusCase[] cases = [.. AmqpTerminusCorpus.Cases.Where(item => item.Rule == rule.Id)];

            entries.Add(new AmqpComplianceEntry(
                AmqpComplianceMap.Terminus,
                rule.Id,
                rule.Statement,
                rule.Reference,
                rule.Forbids,
                false,
                [.. cases.Where(item => item.Refusal is null).Select(item => item.Name)],
                [.. cases.Where(item => item.Refusal is not null).Select(item => item.Name)]));
        }

        Add(entries, AmqpComplianceMap.Transfer, AmqpTransferComplianceTests.Rules);
        Add(entries, AmqpComplianceMap.Transaction, AmqpTransactionComplianceTests.Rules);
        Add(entries, AmqpComplianceMap.Resume, AmqpResumeComplianceTests.Rules);
        Add(entries, AmqpComplianceMap.Performative, AmqpPerformativeTests.Rules);
        Add(entries, AmqpComplianceMap.Connection, AmqpConnectionComplianceTests.Rules);
        Add(entries, AmqpComplianceMap.Session, AmqpSessionComplianceTests.Rules);
        Add(entries, AmqpComplianceMap.Link, AmqpLinkComplianceTests.Rules);
        Add(entries, AmqpComplianceMap.Settlement, AmqpSettlementComplianceTests.Rules);

        return entries;
    }

    private static void Add(List<AmqpComplianceEntry> entries, AmqpComplianceMap map, IReadOnlyList<AmqpComplianceRule> rules)
    {
        foreach (AmqpComplianceRule rule in rules)
        {
            entries.Add(new AmqpComplianceEntry(map, rule.Id, rule.Statement, rule.Reference, rule.Forbids, rule.Leniency, rule.Tests, rule.Refusals));
        }
    }

    private static bool IsRefusal(AmqpTypeEncodingCase item)
    {
        return item.Direction is AmqpTypeEncodingDirection.DecodeRefused or AmqpTypeEncodingDirection.EncodeRefused;
    }

    /// <summary>
    /// Matches where one citation ends and the next begins: <c>; </c> or <c>, and </c> before <c>Part</c>.
    /// </summary>
    [GeneratedRegex(@"(?:, and |; )(?=Part )", RegexOptions.CultureInvariant)]
    private static partial Regex CitationSeparator();

    /// <summary>
    /// Matches one citation: a Part, then one section, or several joined by <c>, </c>, <c> and </c> or <c> to </c>.
    /// </summary>
    [GeneratedRegex(@"^Part (?<part>[1-5]), (?:section (?<section>\d+(?:\.\d+)+)|sections (?<section>\d+(?:\.\d+)+)(?:(?:, | and | to )(?<section>\d+(?:\.\d+)+))+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Citation();
}
