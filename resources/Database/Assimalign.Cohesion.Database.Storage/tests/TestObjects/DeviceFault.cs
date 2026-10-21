namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// The faults a test switches on as one theory's cases (<see cref="DeviceFaults.SwitchOn"/>): each
/// takes the database offline.
/// </summary>
public enum DeviceFault
{
    /// <summary>A write to page 0, the file header's slots, fails (#1268).</summary>
    HeaderWrite,

    /// <summary>A durable flush of the journal fails (#1243).</summary>
    JournalFlush,

    /// <summary>A write of the journal, the drain of its append buffer, fails (#1252).</summary>
    JournalWrite,
}
