using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>A journal checkpoint admission wait was canceled, or admission was refused while a caller retained a monitor or metadata lease.</summary>
/// <remarks>Caller cancellation retains its token. Models distinguish caller cancellation from a statement deadline or non-waiting refusal.</remarks>
public sealed class TransactionAdmissionCanceledException : OperationCanceledException
{
    private static readonly string _message = ErrorMessages.JournalAdmissionCanceled;

    internal TransactionAdmissionCanceledException(CancellationToken cancellationToken, Exception? cause = null)
        : base(_message, cause, cancellationToken)
    {
    }
}
