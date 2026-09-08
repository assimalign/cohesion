using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal static class TrustedIssuerDocument
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static IReadOnlyList<TrustedIssuer> Parse(ReadOnlyMemory<byte> content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("issuers", out JsonElement issuers) ||
            issuers.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The trusted-issuers document must be an object containing an 'issuers' array.");
        }

        var result = new List<TrustedIssuer>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in issuers.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("issuer", out JsonElement issuerProperty) ||
                issuerProperty.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("trustKey", out JsonElement keyProperty))
            {
                throw new InvalidDataException(
                    "Every trusted issuer must contain string 'issuer' and object 'trustKey' properties.");
            }

            string issuer = issuerProperty.GetString()!;
            if (!names.Add(issuer))
            {
                throw new InvalidDataException(
                    $"Trusted issuer '{issuer}' occurs more than once in the document.");
            }

            try
            {
                result.Add(new TrustedIssuer(issuer, keyProperty));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Trusted issuer '{issuer}' has an invalid public key: {exception.Message}",
                    exception);
            }
        }

        return result;
    }

    public static byte[] Write(IReadOnlyList<TrustedIssuer> issuers)
    {
        ArgumentNullException.ThrowIfNull(issuers);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("issuers");
            writer.WriteStartArray();
            for (int index = 0; index < issuers.Count; index++)
            {
                TrustedIssuer issuer = issuers[index]
                    ?? throw new ArgumentException(
                        $"Trusted issuer at index {index} is null.",
                        nameof(issuers));
                writer.WriteStartObject();
                writer.WriteString("issuer", issuer.Issuer);
                writer.WritePropertyName("trustKey");
                issuer.PublicKey.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static async Task WriteFileAsync(
        string path,
        IReadOnlyList<TrustedIssuer> issuers,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidDataException($"Trusted-issuer path '{path}' has no directory.");
        }

        Directory.CreateDirectory(directory);
        byte[] content = Write(issuers);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, PrivateFileMode);
            }
        }
        finally
        {
            Array.Clear(content);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
