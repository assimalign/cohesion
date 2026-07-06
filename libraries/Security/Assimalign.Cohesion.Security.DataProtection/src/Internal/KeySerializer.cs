using System;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Serializes a <see cref="ManagedKey"/> to and from the opaque bytes of a
/// <see cref="KeyDocument"/>. The format is a small, hand-written, line-oriented text document
/// (no reflection-based serializer), so it stays AOT/trim-safe and remains debuggable on disk.
/// </summary>
/// <remarks>
/// v1 stores the master secret base64-encoded <em>in the clear</em>; at-rest encryption of the
/// document is a tracked follow-up. The repository medium is responsible for confidentiality
/// until then (for the file system, that means directory permissions).
/// </remarks>
internal static class KeySerializer
{
    private const string Header = "cohesion-dpk/1";

    /// <summary>Serializes <paramref name="key"/> into a repository document named by its key id.</summary>
    public static KeyDocument Serialize(ManagedKey key)
    {
        StringBuilder builder = new(256);
        builder.Append(Header).Append('\n');
        builder.Append("id=").Append(key.KeyId.ToString("D", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("created=").Append(key.CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("activated=").Append(key.ActivatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("expires=").Append(key.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("revoked=").Append(key.IsRevoked ? '1' : '0').Append('\n');
        builder.Append("material=").Append(Convert.ToBase64String(key.Master)).Append('\n');

        byte[] content = Encoding.UTF8.GetBytes(builder.ToString());
        return new KeyDocument(key.KeyId.ToString("D", CultureInfo.InvariantCulture), content);
    }

    /// <summary>
    /// Attempts to parse a document's <paramref name="content"/> back into a key. Returns
    /// <see langword="false"/> for any malformed or foreign document so a single unreadable
    /// file cannot wedge the whole ring.
    /// </summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> content, out ManagedKey? key)
    {
        key = null;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(content);
        }
        catch (ArgumentException)
        {
            return false;
        }

        Guid id = default;
        long created = 0, activated = 0, expires = 0;
        bool revoked = false;
        byte[]? master = null;
        bool sawHeader = false, sawId = false, sawCreated = false, sawActivated = false, sawExpires = false, sawMaterial = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            if (!sawHeader)
            {
                if (line != Header)
                {
                    return false;
                }
                sawHeader = true;
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            string name = line.Substring(0, eq);
            string value = line.Substring(eq + 1);

            switch (name)
            {
                case "id":
                    if (!Guid.TryParseExact(value, "D", out id))
                    {
                        return false;
                    }
                    sawId = true;
                    break;
                case "created":
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out created))
                    {
                        return false;
                    }
                    sawCreated = true;
                    break;
                case "activated":
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out activated))
                    {
                        return false;
                    }
                    sawActivated = true;
                    break;
                case "expires":
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out expires))
                    {
                        return false;
                    }
                    sawExpires = true;
                    break;
                case "revoked":
                    revoked = value == "1";
                    break;
                case "material":
                    try
                    {
                        master = Convert.FromBase64String(value);
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
                    sawMaterial = true;
                    break;
                default:
                    // Ignore unknown keys for forward compatibility.
                    break;
            }
        }

        if (!sawHeader || !sawId || !sawCreated || !sawActivated || !sawExpires || !sawMaterial
            || master is null || master.Length != ManagedKey.MasterLength)
        {
            return false;
        }

        key = new ManagedKey(
            id,
            DateTimeOffset.FromUnixTimeMilliseconds(created),
            DateTimeOffset.FromUnixTimeMilliseconds(activated),
            DateTimeOffset.FromUnixTimeMilliseconds(expires),
            revoked,
            master);
        return true;
    }
}
