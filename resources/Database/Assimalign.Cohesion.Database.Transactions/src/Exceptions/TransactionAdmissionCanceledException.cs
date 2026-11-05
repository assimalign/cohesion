using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>A cancellation or statement deadline ended a wait for journal checkpoint admission.</summary>
/// <remarks>Caller cancellation retains its token. A model with a statement timer maps this condition to a coded deadline refusal only when its timer, rather than its caller, canceled.</remarks>
public sealed class TransactionAdmissionCanceledException : OperationCanceledException
{
    private static readonly string _message = ErrorMessages.JournalAdmissionCanceled;

    internal TransactionAdmissionCanceledException(CancellationToken cancellationToken, Exception cause)
        : base(_message, cause, cancellationToken)
    {
    }
}
