using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a single decoded journal record.
/// </summary>
/// <param name="Lsn">Monotonically increasing log sequence number.</param>
/// <param name="TimestampUtc">Timestamp when the record was written.</param>
/// <param name="TransactionId">Transaction identifier.</param>
/// <param name="RecordType">Record type.</param>
/// <param name="ModelName">Database model identifier (Sql, Document, Graph, etc.).</param>
/// <param name="ResourceName">Logical resource name (table, collection, graph, etc.).</param>
/// <param name="OperationName">Logical operation name.</param>
/// <param name="Payload">Operation payload bytes.</param>
public readonly record struct JournalRecord(
    long Lsn,
    DateTimeOffset TimestampUtc,
    JournalTransactionId TransactionId,
    JournalRecordType RecordType,
    string ModelName,
    string ResourceName,
    string OperationName,
    ReadOnlyMemory<byte> Payload);
