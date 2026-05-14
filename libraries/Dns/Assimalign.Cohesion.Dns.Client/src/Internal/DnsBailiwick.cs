using System;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Bailiwick checks used by the iterative resolver. The "bailiwick" of an authoritative
/// server is the zone it serves; an authoritative server may only authoritatively answer
/// queries inside that zone. Records returned for names outside the bailiwick are spoofed
/// or buggy and must be dropped (RFC 8499 &#167; 6 introduces the term; RFC 8806 documents
/// the in-bailiwick test more concretely).
/// </summary>
internal static class DnsBailiwick
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is inside the bailiwick of
    /// <paramref name="zone"/> &#8211; i.e. it equals the zone or is one of its subdomains.
    /// Comparison is case-insensitive per RFC 1035 &#167; 2.3.3.
    /// </summary>
    public static bool IsInBailiwick(DnsName name, DnsName zone)
    {
        if (zone.IsRoot)
        {
            // The root is ancestor of every name.
            return true;
        }
        if (name.Equals(zone))
        {
            return true;
        }
        return IsStrictSubdomainOf(name, zone);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is a strict subdomain of
    /// <paramref name="parent"/> &#8211; longer by at least one label and sharing
    /// <paramref name="parent"/>'s suffix.
    /// </summary>
    public static bool IsStrictSubdomainOf(DnsName name, DnsName parent)
    {
        if (parent.IsRoot)
        {
            // Root has no labels; every non-root name is strictly below it.
            return !name.IsRoot;
        }

        string[] nameLabels = name.GetLabels();
        string[] parentLabels = parent.GetLabels();

        if (nameLabels.Length <= parentLabels.Length)
        {
            return false;
        }

        // Compare from the rightmost label backwards.
        for (int i = 1; i <= parentLabels.Length; i++)
        {
            if (!string.Equals(
                    nameLabels[nameLabels.Length - i],
                    parentLabels[parentLabels.Length - i],
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Returns the parent zone of <paramref name="name"/>. The root's parent is the root.
    /// </summary>
    public static DnsName Parent(DnsName name)
    {
        if (name.IsRoot)
        {
            return DnsName.Root;
        }
        string[] labels = name.GetLabels();
        if (labels.Length <= 1)
        {
            return DnsName.Root;
        }
        return new DnsName(string.Join('.', labels, 1, labels.Length - 1));
    }

    /// <summary>
    /// Counts the labels in <paramref name="name"/>, not including the root label. The root
    /// itself has zero labels.
    /// </summary>
    public static int LabelCount(DnsName name) => name.GetLabels().Length;

    /// <summary>
    /// Returns the closest enclosing zone of <paramref name="qname"/> that contains all the
    /// NS records in <paramref name="referral"/>. Used to derive the next-step authority
    /// during iterative resolution: the largest delegation that still covers QNAME.
    /// </summary>
    /// <returns>The zone the NS records collectively delegate, or <see langword="null"/> when
    /// the NS records don't agree on a single zone or none cover QNAME.</returns>
    public static DnsName? ZoneOfNsRecords(System.Collections.Generic.IReadOnlyList<DnsRecord> ns, DnsName qname)
    {
        if (ns.Count == 0)
        {
            return null;
        }

        DnsName? candidate = null;
        foreach (DnsRecord rr in ns)
        {
            if (rr.Type != DnsRecordType.NS)
            {
                continue;
            }
            if (candidate is null)
            {
                candidate = rr.Name;
            }
            else if (!candidate.Value.Equals(rr.Name))
            {
                // NS RRset must share an owner name; mixed owners mean the response can't be
                // a single coherent referral.
                return null;
            }
        }
        if (candidate is null)
        {
            return null;
        }
        DnsName zone = candidate.Value;
        if (!IsInBailiwick(qname, zone))
        {
            return null;
        }
        return zone;
    }
}
