using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Storage;

public sealed partial class DocumentStorage
{
    /// <summary>Validates one UTF-8 JSON value against the persisted document format.</summary>
    /// <param name="content">The complete JSON document.</param>
    /// <exception cref="JsonException">JSON is malformed, duplicates a property, or contains a number outside the decimal domain.</exception>
    public static void ValidateContent(ReadOnlyMemory<byte> content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 128 });
        Validate(document.RootElement);
    }

    /// <summary>Validates and writes a complete JSON value as a transactional chunk chain.</summary>
    /// <param name="coordinator">The coordinator bound to this storage.</param>
    /// <param name="context">The active logical transaction; this method does not commit it.</param>
    /// <param name="content">One UTF-8 JSON value.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The immutable content reference for catalog publication.</returns>
    /// <exception cref="JsonException">The content is outside the document format.</exception>
    public async ValueTask<DocumentContentReference> WriteContentAsync(TransactionCoordinator coordinator,
        ITransactionContext context, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContent(content);
        DocumentContentReference result = default;
        await using (var write = OpenWrite(coordinator, context, reference =>
        {
            result = reference;
            return default;
        }, () => coordinator.RollbackAsync(context), cancellationToken))
        {
            await write.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>Reads and integrity-checks one complete content chain.</summary>
    /// <param name="content">The content reference selected through a visible catalog version.</param>
    /// <returns>The exact originally supplied UTF-8 bytes.</returns>
    /// <exception cref="StorageCorruptionException">The chain length, checksum, or JSON format is invalid.</exception>
    public ReadOnlyMemory<byte> ReadContent(DocumentContentReference content)
    {
        if (content.Length <= 0 || content.Length > int.MaxValue)
        {
            throw new StorageCorruptionException("Document content length must be positive and fit in a memory buffer.");
        }
        using var stream = OpenRead(content);
        var result = new byte[(int)content.Length];
        stream.ReadExactly(result);
        try
        {
            ValidateContent(result);
        }
        catch (JsonException error)
        {
            throw new StorageCorruptionException($"Stored document is outside the JSON format: {error.Message}");
        }
        return result;
    }

    /// <summary>Inserts an unstamped physical index registration on dedicated owner-one pages.</summary>
    /// <param name="transaction">The statement bracket that changes the tree root.</param>
    /// <param name="entry">The registration, including the zero stamp prefix.</param>
    /// <returns>The physical registration location.</returns>
    public (PageId PageId, int SlotIndex) InsertIndexRegistration(IStorageTransaction transaction, ReadOnlySpan<byte> entry)
        => InsertRecord(transaction, 1, entry);

    private static void Validate(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException($"Duplicate document property '{property.Name}'.");
                    }
                    Validate(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Validate(item);
                }
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDecimal(out var number)
                    || NormalizeNumber(element.GetRawText()) != NormalizeNumber(number.ToString("G29", CultureInfo.InvariantCulture)))
                {
                    throw new JsonException("Document numbers must be exactly representable as System.Decimal without rounding or underflow.");
                }
                break;
        }
    }

    private static (bool Negative, string Digits, BigInteger Exponent) NormalizeNumber(string text)
    {
        int exponentStart = text.IndexOfAny(['e', 'E']);
        BigInteger exponent = exponentStart < 0 ? BigInteger.Zero
            : BigInteger.Parse(text.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string significand = exponentStart < 0 ? text : text[..exponentStart];
        bool negative = significand[0] == '-';
        if (negative) { significand = significand[1..]; }
        int point = significand.IndexOf('.');
        if (point >= 0)
        {
            exponent -= significand.Length - point - 1;
            significand = significand.Remove(point, 1);
        }
        significand = significand.TrimStart('0');
        if (significand.Length == 0) { return (false, "0", BigInteger.Zero); }
        int trimmed = significand.TrimEnd('0').Length;
        exponent += significand.Length - trimmed;
        return (negative, significand[..trimmed], exponent);
    }
}
