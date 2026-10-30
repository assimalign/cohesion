using System;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

internal sealed class TransactionUndoException : Exception
{
    internal TransactionUndoException(string code, string message) : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
