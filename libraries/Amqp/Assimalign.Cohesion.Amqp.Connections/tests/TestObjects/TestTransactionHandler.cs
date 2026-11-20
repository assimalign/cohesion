using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A transactional resource for coordinator tests: it numbers transactions from one, records every declare
/// and discharge, and refuses or fails where a test asks it to.
/// </summary>
internal sealed class TestTransactionHandler : IAmqpTransactionHandler
{
    private readonly Lock _sync = new();
    private readonly List<AmqpDeclare> _declares = [];
    private readonly List<(byte[] TransactionId, bool Fail)> _discharges = [];
    private readonly TaskCompletionSource _rolledBack = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _next;

    /// <summary>
    /// Gets or sets the identifier the next declare returns instead of the next number.
    /// </summary>
    public Func<int, byte[]>? CreateTransactionId { get; set; }

    /// <summary>
    /// Gets or sets the exception a declare throws, or <see langword="null"/> to declare.
    /// </summary>
    public Exception? DeclareFailure { get; set; }

    /// <summary>
    /// Gets or sets the exception a discharge that commits throws, or <see langword="null"/> to commit.
    /// </summary>
    public Exception? CommitFailure { get; set; }

    /// <summary>
    /// Gets or sets the exception every discharge that rolls back throws, after it is recorded, or
    /// <see langword="null"/> to roll back.
    /// </summary>
    public Exception? RollbackFailure { get; set; }

    /// <summary>
    /// Gets or sets the number of rollbacks after which <see cref="RolledBack"/> completes.
    /// </summary>
    public int ExpectedRollbacks { get; set; } = 1;

    /// <summary>
    /// Gets a task that completes once <see cref="ExpectedRollbacks"/> rollbacks happened.
    /// </summary>
    public Task RolledBack => _rolledBack.Task;

    public IReadOnlyList<AmqpDeclare> Declares
    {
        get
        {
            lock (_sync)
            {
                return [.. _declares];
            }
        }
    }

    public IReadOnlyList<(byte[] TransactionId, bool Fail)> Discharges
    {
        get
        {
            lock (_sync)
            {
                return [.. _discharges];
            }
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> DeclareAsync(AmqpDeclare declare, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _declares.Add(declare);

            if (DeclareFailure is not null)
            {
                throw DeclareFailure;
            }

            _next++;

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(CreateTransactionId?.Invoke(_next) ?? [(byte)_next]);
        }
    }

    public ValueTask DischargeAsync(ReadOnlyMemory<byte> transactionId, bool fail, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _discharges.Add((transactionId.ToArray(), fail));

            if (!fail && CommitFailure is not null)
            {
                throw CommitFailure;
            }

            if (fail && _discharges.FindAll(static entry => entry.Fail).Count >= ExpectedRollbacks)
            {
                _rolledBack.TrySetResult();
            }

            if (fail && RollbackFailure is not null)
            {
                throw RollbackFailure;
            }

            return ValueTask.CompletedTask;
        }
    }
}
