using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Keeps resources' deployment records where the platform keeps its deployments, and names where each resource's
/// instances read them.
/// </summary>
/// <remarks>
/// <para>
/// The store is the only place a record lives. A gateway holds no copy, so a gateway restart reads the record back
/// from here, and an instance that starts or scales out reads the current record when it starts.
/// </para>
/// <para>
/// Local and in-process gateways keep records as files (<see cref="FileResourceRecordStore"/>); a platform gateway
/// keeps them in the platform's own objects, such as Kubernetes ConfigMaps, and delivers them as one directory.
/// </para>
/// </remarks>
public interface IResourceRecordStore
{
    /// <summary>
    /// Gets the directory a resource's instances read as <see cref="AppEnvironment.Variables.ConfigDirectory"/>, in their own
    /// file system.
    /// </summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <returns>The absolute directory path.</returns>
    string GetInstanceDirectory(ApplicationName application, ResourceName resource);

    /// <summary>Reads a resource's record.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when none has been written.</returns>
    /// <exception cref="System.IO.InvalidDataException">A stored layer is not a valid layer file.</exception>
    ValueTask<ResourceRecord?> ReadAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a resource's record when its stored version is the expected one.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="record">The record to store; layers it lacks are removed.</param>
    /// <param name="expectedVersion">
    /// The <see cref="ResourceRecord.Version"/> the stored record must have, or <see langword="null"/> to write
    /// unconditionally.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the record was written; <see langword="false"/> when the stored version moved.</returns>
    ValueTask<bool> TryWriteAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceRecord record,
        string? expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a resource's record, claims included, when the resource is deleted.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns>A task that completes when the record is gone.</returns>
    ValueTask DeleteAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default);
}
