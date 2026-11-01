using System;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

internal sealed class TransactionUndoException : Exception
{
    internal TransactionUndoException(string code, string message, Exception? innerException = null) : base(message, innerException)
    {
        Code = code;
    }

    internal string Code { get; }
}
