using System;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Destination errors must unwind the snapshot without being mistaken for a database I/O
// failure. Keep the cause outside InnerException until the public export seam rethrows it.
internal sealed class SqlCopyDeliveryException : Exception
{
    private readonly ExceptionDispatchInfo _failure;
    internal SqlCopyDeliveryException(Exception failure) : base(ErrorMessages.CopyDeliveryFailed)
    { _failure = ExceptionDispatchInfo.Capture(failure); }
    internal void Rethrow() => _failure.Throw();
}
