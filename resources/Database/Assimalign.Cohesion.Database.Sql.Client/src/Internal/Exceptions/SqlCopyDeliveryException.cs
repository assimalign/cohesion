using System;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Database.Sql.Client.Internal;

// A caller-owned destination is not the protocol transport. Its failure bypasses the
// shared transport-I/O translator, while still making an incomplete exchange unusable.
internal sealed class SqlCopyDeliveryException : Exception
{
    private readonly ExceptionDispatchInfo _failure;
    internal SqlCopyDeliveryException(Exception failure) : base(ErrorMessages.CopyDeliveryFailed)
    { _failure = ExceptionDispatchInfo.Capture(failure); }
    internal Exception Failure => _failure.SourceException;
    internal void Rethrow() => _failure.Throw();
}
