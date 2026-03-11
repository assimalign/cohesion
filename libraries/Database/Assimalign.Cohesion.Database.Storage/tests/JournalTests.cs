using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class JournalTests
{
	[Fact]
	[DisplayName("Cohesion Test [Database.Storage] - Journal: Should assign sequential LSNs and persist all records")]
	public void Journal_ShouldAssignSequentialLsns_AndPersistAllRecords()
	{
		using var stream = new MemoryStream();
		using var journal = new StreamJournalLogger(stream, leaveOpen: true);

		JournalTransactionId transactionId = journal.BeginTransaction("Sql", "Users");
		long op1Lsn = journal.AppendOperation(transactionId, "INSERT", Encoding.UTF8.GetBytes("row-1"));
		long op2Lsn = journal.AppendOperation(transactionId, "UPDATE", Encoding.UTF8.GetBytes("row-2"));
		journal.CommitTransaction(transactionId);

		var records = journal.ReadAll();

		Assert.Equal(4, records.Count);
		Assert.Equal(new[] { 1L, 2L, 3L, 4L }, records.Select(x => x.Lsn).ToArray());
		Assert.Equal(JournalRecordType.BeginTransaction, records[0].RecordType);
		Assert.Equal(JournalRecordType.Operation, records[1].RecordType);
		Assert.Equal(JournalRecordType.Operation, records[2].RecordType);
		Assert.Equal(JournalRecordType.CommitTransaction, records[3].RecordType);
		Assert.Equal(2L, op1Lsn);
		Assert.Equal(3L, op2Lsn);
	}

	[Fact]
	[DisplayName("Cohesion Test [Database.Storage] - Journal: Recover should return only committed operations")]
	public void Journal_RecoverCommittedOperations_ShouldReturnOnlyCommittedOperations()
	{
		using var stream = new MemoryStream();
		using var journal = new StreamJournalLogger(stream, leaveOpen: true);

		JournalTransactionId committed = journal.BeginTransaction("Document", "Profiles");
		journal.AppendOperation(committed, "UPSERT", Encoding.UTF8.GetBytes("committed"));
		journal.CommitTransaction(committed);

		JournalTransactionId open = journal.BeginTransaction("Document", "Profiles");
		journal.AppendOperation(open, "UPSERT", Encoding.UTF8.GetBytes("not-committed"));

		var replayRecords = journal.RecoverCommittedOperations();

		Assert.Single(replayRecords);
		Assert.Equal(committed, replayRecords[0].TransactionId);
		Assert.Equal("UPSERT", replayRecords[0].OperationName);
		Assert.Equal("committed", Encoding.UTF8.GetString(replayRecords[0].Payload.Span));
	}

	[Fact]
	[DisplayName("Cohesion Test [Database.Storage] - Journal: Rollback should exclude transaction operations from recovery")]
	public void Journal_Rollback_ShouldExcludeOperationsFromRecovery()
	{
		using var stream = new MemoryStream();
		using var journal = new StreamJournalLogger(stream, leaveOpen: true);

		JournalTransactionId rollbackTransaction = journal.BeginTransaction("Graph", "Edges");
		journal.AppendOperation(rollbackTransaction, "ADD_EDGE", Encoding.UTF8.GetBytes("edge-1"));
		journal.RollbackTransaction(rollbackTransaction);

		var replayRecords = journal.RecoverCommittedOperations();
		Assert.Empty(replayRecords);
	}

	[Fact]
	[DisplayName("Cohesion Test [Database.Storage] - Journal: Reopened journal should recover committed operations")]
	public void Journal_ReopenedInstance_ShouldRecoverCommittedOperations()
	{
		using var stream = new MemoryStream();

		using (var writerJournal = new StreamJournalLogger(stream, leaveOpen: true))
		{
			JournalTransactionId transactionId = writerJournal.BeginTransaction("Sql", "Orders");
			writerJournal.AppendOperation(transactionId, "INSERT", Encoding.UTF8.GetBytes("order-1"));
			writerJournal.CommitTransaction(transactionId);
		}

		stream.Position = 0;

		using var recoveryJournal = new StreamJournalLogger(stream, leaveOpen: true);
		var replayRecords = recoveryJournal.RecoverCommittedOperations();

		Assert.Single(replayRecords);
		Assert.Equal("Sql", replayRecords[0].ModelName);
		Assert.Equal("Orders", replayRecords[0].ResourceName);
		Assert.Equal("INSERT", replayRecords[0].OperationName);
		Assert.Equal("order-1", Encoding.UTF8.GetString(replayRecords[0].Payload.Span));
	}
}
