using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Scalar results retain the tuple codec's one array allocation per row. Composite columns
// are marked in ResultHeader; only those columns have a value tag and optional length.
internal sealed class GraphResultRowEncoder : IDisposable
{
    private readonly DatabaseKeyWriter _scalarWriter;
    private readonly IReadOnlyList<bool>? _compositeColumns;
    private readonly MemoryStream? _stream;
    private readonly BinaryWriter? _writer;

    internal GraphResultRowEncoder(DatabaseKeyWriter scalarWriter, IReadOnlyList<bool>? compositeColumns)
    {
        _scalarWriter = scalarWriter;
        _compositeColumns = compositeColumns;
        if (compositeColumns?.Any(value => value) == true)
        { _stream = new MemoryStream(); _writer = new BinaryWriter(_stream); }
    }

    internal byte[] Encode(DatabaseRow row)
    {
        _scalarWriter.Reset();
        if (_stream is null)
        {
            for (int i = 0; i < row.FieldCount; i++) { DatabaseValueCodec.Append(_scalarWriter, row.GetValue(i)); }
            return _scalarWriter.ToArray();
        }
        _stream.SetLength(0); _stream.Position = 0;
        for (int i = 0; i < row.FieldCount; i++)
        {
            var value = row.GetValue(i);
            bool tagged = _compositeColumns![i];
            if (tagged && (GraphCompositeValues.IsComposite(value) || value is byte or ushort or uint or ulong or decimal))
            {
                _writer!.Write((byte)1);
                long lengthOffset = _stream.Position; _writer.Write(0);
                int count = 0;
                GraphCompositeEncoding.Write(_writer, value, 0, ref count, _scalarWriter);
                long end = _stream.Position;
                _stream.Position = lengthOffset; _writer.Write(checked((int)(end - lengthOffset - 4))); _stream.Position = end;
            }
            else
            {
                if (tagged) { _writer!.Write((byte)0); }
                _scalarWriter.Reset(); DatabaseValueCodec.Append(_scalarWriter, value);
                _writer!.Write(_scalarWriter.WrittenSpan);
            }
        }
        return _stream.ToArray();
    }

    internal static void Validate(object?[] row, IReadOnlyList<bool> compositeColumns)
    {
        long size = 0;
        try
        {
            for (int i = 0; i < row.Length; i++)
            {
                var value = row[i];
                size += compositeColumns[i] && (GraphCompositeValues.IsComposite(value) || value is byte or ushort or uint or ulong or decimal)
                    ? 5L + GraphCompositeEncoding.Measure(value) : GraphCompositeEncoding.ScalarLength(value) + (compositeColumns[i] ? 1L : 0);
            }
            if (size > GraphValueCodec.MaxPayloadLength) { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
        }
        catch (DatabaseTypeException error)
        { throw new GraphPlanningException("COHDBG013", error.Message, null, error); }
    }

    public void Dispose() { _writer?.Dispose(); _stream?.Dispose(); }
}
