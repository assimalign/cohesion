using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Rezolvr.Hosting.Internal;

internal sealed class RezolvrResourceCommandHandler : IResourceCommandHandler
{
    internal const string AddARecord = "rezolvr.add-a-record";
    internal const string AddCnameRecord = "rezolvr.add-cname-record";
    private readonly RezolvrRecordRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezolvrResourceCommandHandler"/> class.
    /// </summary>
    /// <param name="kind">The resource command kind this handler serves.</param>
    /// <param name="repository">The record repository that applies and deletes the commanded records.</param>
    public RezolvrResourceCommandHandler(string kind, RezolvrRecordRepository repository)
    {
        Kind = kind;
        _repository = repository;
    }

    public string Kind { get; }

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await _repository.MutateAsync(command, delete: false, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await _repository.MutateAsync(command, delete: true, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }
}
