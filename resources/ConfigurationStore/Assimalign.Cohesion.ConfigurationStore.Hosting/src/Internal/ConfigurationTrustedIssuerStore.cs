using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

internal static class ConfigurationTrustedIssuerStore
{
    private const string TrustedIssuerFileName = "trusted-issuers.json";

    internal static async Task<IReadOnlyList<ConfigurationTrustedIssuer>> LoadAsync(
        string dataPath,
        ResourceContext? context,
        bool requireAuthentication,
        CancellationToken cancellationToken)
    {
        string trustDirectory = Path.Combine(dataPath, "trust");
        string path = Path.Combine(trustDirectory, TrustedIssuerFileName);
        if (File.Exists(path))
        {
            byte[] content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ConfigurationTrustedIssuer> persisted = Parse(content, path);
            if (!requireAuthentication)
            {
                return persisted;
            }

            ConfigurationTrustedIssuer ambient = CreateAmbientIssuer(context);
            var reconciled = new List<ConfigurationTrustedIssuer>(persisted.Count + 1);
            bool changed = false;
            bool found = false;
            for (int index = 0; index < persisted.Count; index++)
            {
                ConfigurationTrustedIssuer issuer = persisted[index];
                if (!string.Equals(issuer.Issuer, ambient.Issuer, StringComparison.Ordinal))
                {
                    reconciled.Add(issuer);
                    continue;
                }

                found = true;
                if (string.Equals(issuer.KeyId, ambient.KeyId, StringComparison.Ordinal))
                {
                    reconciled.Add(issuer);
                }
                else
                {
                    reconciled.Add(ambient);
                    changed = true;
                }
            }

            if (!found)
            {
                reconciled.Add(ambient);
                changed = true;
            }

            if (changed)
            {
                await WriteAsync(path, reconciled, overwrite: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new ReadOnlyCollection<ConfigurationTrustedIssuer>(reconciled);
        }

        if (!requireAuthentication)
        {
            return Array.Empty<ConfigurationTrustedIssuer>();
        }

        ConfigurationTrustedIssuer seeded = CreateAmbientIssuer(context);

        Directory.CreateDirectory(trustDirectory);
        await WriteAsync(path, [seeded], overwrite: false, cancellationToken).ConfigureAwait(false);
        return new ReadOnlyCollection<ConfigurationTrustedIssuer>([seeded]);
    }

    private static ConfigurationTrustedIssuer CreateAmbientIssuer(ResourceContext? context)
    {
        if (context is null ||
            string.IsNullOrWhiteSpace(context.ApplicationName) ||
            context.ApplicationTrustKey.IsEmpty)
        {
            throw new InvalidOperationException(
                "A gateway-managed ConfigurationStore requires an application name and public application trust key.");
        }

        try
        {
            using JsonDocument keyDocument = JsonDocument.Parse(context.ApplicationTrustKey);
            return new ConfigurationTrustedIssuer(
                context.ApplicationName,
                keyDocument.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The ambient application trust key is not a valid public JWK document.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The ambient application trust key is invalid: {exception.Message}",
                exception);
        }
    }

    private static IReadOnlyList<ConfigurationTrustedIssuer> Parse(
        ReadOnlyMemory<byte> content,
        string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("issuers", out JsonElement issuers) ||
                issuers.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Trusted-issuer document '{path}' must contain an 'issuers' array.");
            }

            var result = new List<ConfigurationTrustedIssuer>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in issuers.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object ||
                    !item.TryGetProperty("issuer", out JsonElement issuerProperty) ||
                    issuerProperty.ValueKind is not JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(issuerProperty.GetString()) ||
                    !item.TryGetProperty("trustKey", out JsonElement keyProperty))
                {
                    throw new InvalidDataException(
                        $"Every entry in trusted-issuer document '{path}' requires 'issuer' and 'trustKey'.");
                }

                string issuer = issuerProperty.GetString()!;
                if (!names.Add(issuer))
                {
                    throw new InvalidDataException(
                        $"Trusted issuer '{issuer}' occurs more than once in '{path}'.");
                }

                try
                {
                    result.Add(new ConfigurationTrustedIssuer(issuer, keyProperty));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"Trusted issuer '{issuer}' in '{path}' is invalid: {exception.Message}",
                        exception);
                }
            }

            return new ReadOnlyCollection<ConfigurationTrustedIssuer>(result);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Trusted-issuer document '{path}' is not valid JSON.",
                exception);
        }
    }

    private static async Task WriteAsync(
        string path,
        IReadOnlyList<ConfigurationTrustedIssuer> issuers,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WritePropertyName("issuers");
                writer.WriteStartArray();
                for (int index = 0; index < issuers.Count; index++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("issuer", issuers[index].Issuer);
                    writer.WritePropertyName("trustKey");
                    issuers[index].PublicKey.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
