using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Stream-backed journal logger implementation.
/// </summary>
public sealed class StreamJournalLogger : JournalLoggerBase
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    /// <summary>
    /// Initializes a stream-backed journal logger.
    /// </summary>
    /// <param name="stream">Readable, writable, seekable stream.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the journal.</param>
    public StreamJournalLogger(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("Journal stream must support read, write, and seek.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates a file-backed journal logger.
    /// </summary>
    /// <param name="path">Journal file path.</param>
    /// <returns>Created journal logger instance.</returns>
    public static StreamJournalLogger FromFile(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        return new StreamJournalLogger(stream);
    }

    /// <inheritdoc />
    protected override void AppendFrame(ReadOnlySpan<byte> frame)
    {
        _stream.Seek(0, SeekOrigin.End);
        _stream.Write(frame);
    }

    /// <inheritdoc />
    protected override void FlushCore(bool forceDurable)
    {
        if (forceDurable && _stream is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
            return;
        }

        _stream.Flush();
    }

    /// <inheritdoc />
    protected override IReadOnlyList<JournalRecord> ReadAllInternal()
    {
        long originalPosition = _stream.Position;
        try
        {
            _stream.Seek(0, SeekOrigin.Begin);
            var records = new List<JournalRecord>();

            while (_stream.Position < _stream.Length)
            {
                var lengthBuffer = new byte[sizeof(int)];
                int lengthRead = _stream.Read(lengthBuffer, 0, lengthBuffer.Length);
                if (lengthRead != lengthBuffer.Length)
                {
                    break;
                }

                int frameLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                if (frameLength <= sizeof(int) + sizeof(uint))
                {
                    break;
                }

                var frame = new byte[frameLength];
                int frameRead = _stream.Read(frame, 0, frame.Length);
                if (frameRead != frame.Length)
                {
                    break;
                }

                var frameSpan = frame.AsSpan();
                int offset = 0;

                int magic = BinaryPrimitives.ReadInt32LittleEndian(frameSpan.Slice(offset, sizeof(int)));
                offset += sizeof(int);

                if (magic != Magic)
                {
                    break;
                }

                int bodyLength = frameLength - sizeof(int) - sizeof(uint);
                ReadOnlySpan<byte> body = frameSpan.Slice(offset, bodyLength);
                offset += bodyLength;

                uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(frameSpan.Slice(offset, sizeof(uint)));
                uint actualChecksum = Crc32.Compute(body);
                if (expectedChecksum != actualChecksum)
                {
                    break;
                }

                records.Add(ParseRecord(body));
            }

            return records;
        }
        finally
        {
            _stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static JournalRecord ParseRecord(ReadOnlySpan<byte> body)
    {
        int offset = 0;

        int version = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset, sizeof(int)));
        offset += sizeof(int);

        if (version != CurrentVersion)
        {
            throw new JournalException($"Unsupported journal version: {version}.");
        }

        long lsn = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(offset, sizeof(long)));
        offset += sizeof(long);

        long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(offset, sizeof(long)));
        offset += sizeof(long);

        var transactionBytes = body.Slice(offset, 16);
        var transactionId = new JournalTransactionId(new Guid(transactionBytes));
        offset += 16;

        var recordType = (JournalRecordType)body[offset];
        offset += sizeof(byte);

        string modelName = ReadLengthPrefixedString(body, ref offset);
        string resourceName = ReadLengthPrefixedString(body, ref offset);
        string operationName = ReadLengthPrefixedString(body, ref offset);
        byte[] payload = ReadLengthPrefixedBytes(body, ref offset);

        return new JournalRecord(
            lsn,
            new DateTimeOffset(timestampTicks, TimeSpan.Zero),
            transactionId,
            recordType,
            modelName,
            resourceName,
            operationName,
            payload);
    }

    private static string ReadLengthPrefixedString(ReadOnlySpan<byte> source, ref int offset)
    {
        byte[] value = ReadLengthPrefixedBytes(source, ref offset);
        return Encoding.UTF8.GetString(value);
    }

    private static byte[] ReadLengthPrefixedBytes(ReadOnlySpan<byte> source, ref int offset)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, sizeof(int)));
        offset += sizeof(int);

        if (length < 0 || offset + length > source.Length)
        {
            throw new JournalException("Invalid journal frame content length.");
        }

        var value = source.Slice(offset, length).ToArray();
        offset += length;
        return value;
    }
}
