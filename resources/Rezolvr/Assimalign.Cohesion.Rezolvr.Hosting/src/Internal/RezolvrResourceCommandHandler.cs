using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

internal sealed class RezolvrResourceCommandHandler(string kind, RezolvrRecordRepository repository) : IResourceCommandHandler
{
    internal const string AddARecord = "rezolvr.add-a-record";
    internal const string AddCnameRecord = "rezolvr.add-cname-record";
    public string Kind { get; } = kind;

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await repository.MutateAsync(command, delete: false, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await repository.MutateAsync(command, delete: true, cancellationToken).ConfigureAwait(false);
        return ReadOnlyMemory<byte>.Empty;
    }
}
