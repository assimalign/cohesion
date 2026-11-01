using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Where a gateway keeps its record between runs: what it deployed, the state recorded since, the
/// instances registered, and the outputs read.
/// </summary>
/// <remarks>
/// <para>
/// A gateway takes its record over once per run (<see cref="AcquireAsync"/>) and saves the whole
/// record after every change (<see cref="SaveAsync"/>); its saves never overlap. Taking the record over
/// fences off every earlier run: from then on the store refuses their saves, so when two runs of one
/// gateway overlap, such as the old and the new gateway of a rolling update or a pipeline run again
/// while the previous one still runs, the newer one's record wins and the older one stops.
/// </para>
/// <para>
/// <see cref="JsonFileApplicationGatewayStore"/> keeps records in files. A platform package backs the
/// store with the platform's own durable storage, and the gateway sees only this contract.
/// </para>
/// </remarks>
public interface IApplicationGatewayStore
{
    /// <summary>
    /// Loads a gateway's record without taking it over, such as for a tool that shows it.
    /// </summary>
    /// <param name="gateway">The gateway's name.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The record, or <see langword="null"/> when the gateway has none because it never ran.</returns>
    ValueTask<ApplicationGatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a gateway's record over for one run, fencing off every run before it.
    /// </summary>
    /// <remarks>
    /// Reading the record and storing the new epoch are one atomic step, so two runs that take the
    /// record over at the same time get different epochs, and the later one's wins.
    /// </remarks>
    /// <param name="gateway">The gateway's name.</param>
    /// <param name="cancellationToken">Cancels taking the record over.</param>
    /// <returns>
    /// The record with an <see cref="ApplicationGatewayRecord.Epoch"/> higher than any run took
    /// before, now stored; a record with no resources when the gateway never ran.
    /// </returns>
    ValueTask<ApplicationGatewayRecord> AcquireAsync(GatewayName gateway, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a gateway's record, replacing the previous one, unless a later run took the record over.
    /// </summary>
    /// <remarks>
    /// Comparing the epochs and writing the record are one atomic step. A record whose
    /// <see cref="ApplicationGatewayRecord.Epoch"/> equals the stored one is the same run saving again.
    /// </remarks>
    /// <param name="record">The record, carrying the epoch of the run that took it over.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>
    /// <see langword="true"/> when the record is stored; <see langword="false"/> when the store holds a
    /// higher epoch, so a later run holds the record and nothing was saved.
    /// </returns>
    ValueTask<bool> SaveAsync(ApplicationGatewayRecord record, CancellationToken cancellationToken = default);
}
