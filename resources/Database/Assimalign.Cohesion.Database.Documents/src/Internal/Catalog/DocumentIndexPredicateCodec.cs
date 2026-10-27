using System.IO;

using Assimalign.Cohesion.Database.Documents.Catalog;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentIndexPredicateCodec
{
    internal static void Write(BinaryWriter writer, DocumentIndexPredicate? predicate)
    {
        writer.Write(predicate?.Kind ?? (byte)0);
        if (predicate is null) { return; }
        if (predicate.Kind <= 3) { DocumentCatalogCodec.WriteString(writer, predicate.Path); }
        if (predicate.Kind == 1)
        {
            writer.Write((byte)predicate.Comparison);
            writer.Write((byte)(predicate.Value switch { null => 1, false => 2, true => 3, decimal => 4, _ => 5 }));
            if (predicate.Value is decimal number) { writer.Write(number); }
            else if (predicate.Value is string text) { DocumentCatalogCodec.WriteString(writer, text); }
        }
        if (predicate.Left is not null) { Write(writer, predicate.Left); }
        if (predicate.Right is not null) { Write(writer, predicate.Right); }
    }

    internal static DocumentIndexPredicate? Read(BinaryReader reader)
    {
        int remaining = 128;
        return ReadNode(reader, 1, ref remaining, optional: true);
    }

    private static DocumentIndexPredicate? ReadNode(BinaryReader reader, int depth, ref int remaining, bool optional = false)
    {
        byte kind = reader.ReadByte();
        if (kind == 0 && optional) { return null; }
        if (kind is < 1 or > 6 || depth > 32 || --remaining < 0)
        { throw new DocumentCatalogException("CD06105", ErrorMessages.IndexDescriptorInvalid); }
        if (kind <= 3)
        {
            string path = DocumentCatalogCodec.ReadString(reader)
                ?? throw new DocumentCatalogException("CD06105", ErrorMessages.IndexDescriptorInvalid);
            if (kind == 2) { return DocumentIndexPredicate.IsNull(path); }
            if (kind == 3) { return DocumentIndexPredicate.IsMissing(path); }
            var operation = (DocumentIndexComparison)reader.ReadByte();
            object? value = reader.ReadByte() switch
            {
                1 => null,
                2 => false,
                3 => true,
                4 => reader.ReadDecimal(),
                5 => DocumentCatalogCodec.ReadString(reader) ?? throw new DocumentCatalogException("CD06105", ErrorMessages.IndexDescriptorInvalid),
                _ => throw new DocumentCatalogException("CD06105", ErrorMessages.IndexDescriptorInvalid)
            };
            return DocumentIndexPredicate.Compare(path, operation, value);
        }
        var left = ReadNode(reader, depth + 1, ref remaining)!;
        if (kind == 6) { return DocumentIndexPredicate.Not(left); }
        var right = ReadNode(reader, depth + 1, ref remaining)!;
        return kind == 4 ? DocumentIndexPredicate.And(left, right) : DocumentIndexPredicate.Or(left, right);
    }
}
