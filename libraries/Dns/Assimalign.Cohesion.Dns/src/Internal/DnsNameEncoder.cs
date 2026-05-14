using System;
using System.Text;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Encodes a <see cref="DnsName"/> into wire format, optionally compressing suffixes that
/// have already appeared in the current message (RFC 1035 &#167; 4.1.4).
/// </summary>
/// <remarks>
/// <para>
/// The encoder walks the name's labels from the apex back to the leaf. For each suffix it
/// either emits a 14-bit pointer into <see cref="DnsWireWriter.LabelOffsets"/> when an
/// earlier occurrence is recorded, or emits the literal label(s) followed by the root
/// terminator. New suffixes are recorded so subsequent names in the same message can
/// reference them.
/// </para>
/// <para>
/// Records that the wire format requires to skip compression (notably the RRSIG signing
/// process) call the no-compress entry point so the canonical form is reproducible.
/// </para>
/// </remarks>
internal static class DnsNameEncoder
{
    /// <summary>
    /// Writes <paramref name="name"/> in wire format using compression where possible.
    /// </summary>
    public static void Write(ref DnsWireWriter writer, DnsName name)
        => WriteCore(ref writer, name, compress: true);

    /// <summary>
    /// Writes <paramref name="name"/> in wire format without consulting the compression
    /// table. Used for canonical-form serialization (DNSSEC, future feature) where
    /// compression is forbidden.
    /// </summary>
    public static void WriteUncompressed(ref DnsWireWriter writer, DnsName name)
        => WriteCore(ref writer, name, compress: false);

    private static void WriteCore(ref DnsWireWriter writer, DnsName name, bool compress)
    {
        string[] labels = name.GetLabels();

        // Root name is just the zero terminator.
        if (labels.Length == 0)
        {
            writer.WriteUInt8(0);
            return;
        }

        // Walk labels from leaf to root, building lowercase suffixes for the compression
        // table. We emit labels in their original case but key the table by lowercase to
        // match RFC 1035 §2.3.3 case-insensitive equality.
        for (int i = 0; i < labels.Length; i++)
        {
            string label = labels[i];
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            if (labelBytes.Length > 63)
            {
                DnsException.ThrowMalformed(
                    $"label '{label}' exceeds the RFC 1035 limit of 63 octets in wire form");
            }

            string suffixKey = BuildSuffixKey(labels, i);

            // Compression: if this suffix already appears in the message and the offset fits
            // in 14 bits, emit a pointer and stop.
            if (compress && writer.LabelOffsets.TryGetValue(suffixKey, out int existingOffset))
            {
                if (existingOffset < 0x4000)
                {
                    writer.WriteUInt16((ushort)(0xC000 | existingOffset));
                    return;
                }
            }

            // Record THIS suffix's offset before writing, so a later name can point to it.
            if (compress && writer.Position < 0x4000)
            {
                writer.LabelOffsets[suffixKey] = writer.Position;
            }

            writer.WriteUInt8((byte)labelBytes.Length);
            writer.WriteBytes(labelBytes);
        }

        // Root terminator.
        writer.WriteUInt8(0);
    }

    /// <summary>
    /// Builds the lowercase dotted suffix starting at <paramref name="startIndex"/>. Used as
    /// the compression-table key so equivalent suffixes match regardless of case.
    /// </summary>
    private static string BuildSuffixKey(string[] labels, int startIndex)
    {
        if (startIndex == labels.Length - 1)
        {
            return labels[startIndex].ToLowerInvariant();
        }

        StringBuilder builder = new();
        for (int i = startIndex; i < labels.Length; i++)
        {
            if (i > startIndex)
            {
                builder.Append('.');
            }
            builder.Append(labels[i].ToLowerInvariant());
        }
        return builder.ToString();
    }
}
