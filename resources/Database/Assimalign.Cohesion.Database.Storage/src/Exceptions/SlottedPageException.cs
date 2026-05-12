namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents an error that occurs during slotted page operations,
/// such as when a record does not fit or a deleted slot is accessed.
/// </summary>
public sealed class SlottedPageException : StorageException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SlottedPageException"/> with the specified message.
    /// </summary>
    /// <param name="message">A message describing the error.</param>
    public SlottedPageException(string message)
        : base(message)
    {
    }
}
