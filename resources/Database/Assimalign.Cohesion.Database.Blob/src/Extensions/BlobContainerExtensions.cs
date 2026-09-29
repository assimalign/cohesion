using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Internal;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Reads container ownership without changing the blob container contract.</summary>
public static partial class BlobContainerExtensions
{
    extension(IBlobContainer container)
    {
        /// <summary>Reads the container's ownership from the current operation's catalog snapshot.</summary>
        /// <param name="cancellationToken">Cancels the metadata read.</param>
        /// <returns>
        /// A read-only property bag containing <c>OWNER</c> as <see cref="DatabaseObjectOwner"/>
        /// and <c>OWNING_SCHEMA</c> as a schema name, or null for an ad-hoc container.
        /// </returns>
        /// <exception cref="ArgumentNullException">The container is null.</exception>
        /// <exception cref="DatabaseException">The container is unavailable or does not support ownership discovery.</exception>
        /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
        /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
        /// <remarks>
        /// Session-bound containers use the session's isolation level. The returned property bag is
        /// detached from the catalog; all dictionary mutation operations throw <see cref="NotSupportedException"/>.
        /// Container discovery remains available through <see cref="IBlobDatabase.GetContainersAsync"/>.
        /// </remarks>
        public ValueTask<IReadOnlyDictionary<string, object?>> GetOwnershipAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(container);
            cancellationToken.ThrowIfCancellationRequested();
            return container is BlobContainer implementation
                ? implementation.GetOwnershipAsync(cancellationToken)
                : throw new DatabaseException("This blob container does not support ownership discovery.");
        }
    }
}
