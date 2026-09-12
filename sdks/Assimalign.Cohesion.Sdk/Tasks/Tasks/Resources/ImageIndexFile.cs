using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

#if COHESION_GATEWAY_TASKS
namespace Assimalign.Cohesion.Sdk.Gateway.Tasks;
#else
namespace Assimalign.Cohesion.Sdk.Tasks;
#endif

internal static class ImageIndexFile
{
    internal static string Digest(string value)
    {
        if (!Regex.IsMatch(value, "^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Image digest must be sha256: followed by exactly 64 hexadecimal characters.");
        }

        return value.ToLowerInvariant();
    }

    internal static void Repository(string value)
    {
        const string component = "[a-z0-9]+(?:(?:[.]|_{1,2}|-+)[a-z0-9]+)*";
        string first = value.Split('/')[0];
        if (!Regex.IsMatch(value, "^" + component + "(?:/" + component + ")*$", RegexOptions.CultureInvariant)
            || (value.Contains('/') && (first.Contains('.') || first == "localhost")))
        {
            throw new InvalidDataException($"CohesionContainerRepository '{value}' must be a lowercase OCI repository without a registry authority; components must begin and end with an alphanumeric and use '.', '_', '__', or '-' separators.");
        }
    }

    internal static void Registry(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0
            || value.EndsWith(':') || value.IndexOfAny([' ', '\t', '\r', '\n']) >= 0
            || !Uri.TryCreate("http://" + value, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/")
        {
            throw new InvalidDataException("Container registry must be a bare authority, optionally followed by a repository prefix in CohesionContainerRegistry.");
        }
    }

    internal static string RelativeArchive(string indexPath, string archivePath)
    {
        string directory = PhysicalPath(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        string relative = Path.GetRelativePath(directory, PhysicalPath(archivePath));
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"CohesionContainerArchiveOutputPath '{archivePath}' must be contained beneath image index directory '{directory}'. An archive sink cannot omit archive with a late-bound registry, including when CohesionPackImageArchive=true.");
        }

        return relative.Replace('\\', '/');
    }

    internal static string ResolveArchive(string indexPath, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative[0] is '/' or '\\'
            || Path.IsPathRooted(relative) || (relative.Length > 1 && relative[1] == ':'))
        {
            throw new InvalidDataException("Image archive must be an omitted field or a non-empty contained relative path.");
        }

        string path = Path.GetFullPath(relative.Replace('\\', '/'), Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        RelativeArchive(indexPath, path);
        return path;
    }

    internal static void VerifyArchive(string archive, string digest)
    {
        digest = Digest(digest);
        using FileStream stream = File.OpenRead(archive);
        int first = stream.ReadByte();
        int second = stream.ReadByte();
        stream.Position = 0;
        using Stream tar = first == 0x1f && second == 0x8b ? new GZipStream(stream, CompressionMode.Decompress) : stream;
        using var reader = new TarReader(tar);
        bool foundIndex = false;
        bool foundManifest = false;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (name == "index.json")
            {
                if (foundIndex || entry.DataStream is null)
                {
                    throw new InvalidDataException("OCI archive must have exactly one index.json.");
                }

                using JsonDocument index = JsonDocument.Parse(entry.DataStream);
                JsonElement manifests = index.RootElement.GetProperty("manifests");
                if (manifests.GetArrayLength() != 1 || Digest(manifests[0].GetProperty("digest").GetString() ?? "") != digest)
                {
                    throw new InvalidDataException("GeneratedContainerDigest differs from the OCI archive index.json digest.");
                }

                foundIndex = true;
            }
            else if (name == "blobs/sha256/" + digest[7..])
            {
                if (foundManifest || entry.DataStream is null
                    || "sha256:" + Convert.ToHexStringLower(SHA256.HashData(entry.DataStream)) != digest)
                {
                    throw new InvalidDataException("OCI archive manifest bytes differ from GeneratedContainerDigest.");
                }

                foundManifest = true;
            }
        }
        if (!foundIndex || !foundManifest)
        {
            throw new InvalidDataException("OCI archive is missing index.json or its digest-addressed manifest blob.");
        }
    }

    internal static JsonDocument Read(string path)
    {
        JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        try
        {
            JsonElement root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Name is not ("schema" or "resource" or "repository" or "registry" or "tag" or "digest" or "platform" or "aot" or "baseImage" or "archive"))
                {
                    throw new InvalidDataException($"Unknown or duplicate image field '{property.Name}'.");
                }
            }
            if (Required(root, "schema") != "cohesion/image/v1")
            {
                throw new InvalidDataException("Image schema must be cohesion/image/v1.");
            }

            Required(root, "resource");
            Repository(Required(root, "repository"));
            Digest(Required(root, "digest"));
            Required(root, "baseImage");
            if (!Regex.IsMatch(Required(root, "platform"), "^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?/[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?)?$"))
            {
                throw new InvalidDataException("Image platform must be lowercase os/architecture[/variant].");
            }

            root.GetProperty("aot").GetBoolean();
            if (root.TryGetProperty("registry", out JsonElement registry) && registry.ValueKind != JsonValueKind.Null)
            {
                Registry(Required(root, "registry"));
            }

            if (root.TryGetProperty("tag", out JsonElement tag) && tag.ValueKind != JsonValueKind.Null)
            {
                Required(root, "tag");
            }

            if (root.TryGetProperty("archive", out _))
            {
                ResolveArchive(path, Required(root, "archive"));
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    internal static string Required(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Image field '{name}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static string PhysicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current) : File.Exists(current) ? new FileInfo(current) : null;
            current = info?.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return current;
    }
}
