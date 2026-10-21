using System.Collections.Generic;

using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog;

/// <summary>
/// A consistent, read-only capture of a key-value catalog's format and index registrations,
/// taken by <see cref="KeyValueCatalog.CaptureSnapshot"/>.
/// </summary>
/// <remarks>
/// Both values are captured together and remain unchanged by later catalog writes. The capture
/// owns no storage or disposal lifetime. Not a record: a public positional record would expose a
/// public constructor and a <c>with</c> clone (concrete-types plan, phase 4, #1260).
/// </remarks>
public sealed class KeyValueCatalogSnapshot
{
    internal KeyValueCatalogSnapshot(int entrySpaceFormatVersion, IReadOnlyList<BTreeIndexRegistration> indexRegistrations)
    {
        EntrySpaceFormatVersion = entrySpaceFormatVersion;
        IndexRegistrations = indexRegistrations;
    }

    /// <summary>Gets the entry-space format version at capture time.</summary>
    public int EntrySpaceFormatVersion { get; }

    /// <summary>Gets the physical index registrations at capture time.</summary>
    public IReadOnlyList<BTreeIndexRegistration> IndexRegistrations { get; }
}
