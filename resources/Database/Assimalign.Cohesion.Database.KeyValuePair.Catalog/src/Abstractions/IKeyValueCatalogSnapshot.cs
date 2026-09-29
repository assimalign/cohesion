using System.Collections.Generic;

using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog;

/// <summary>
/// A consistent, read-only capture of a key-value catalog's format and index registrations.
/// </summary>
/// <remarks>
/// Both values are captured together and remain unchanged by later catalog writes.
/// The capture owns no storage or disposal lifetime.
/// </remarks>
public interface IKeyValueCatalogSnapshot
{
    /// <summary>Gets the entry-space format version at capture time.</summary>
    int EntrySpaceFormatVersion { get; }

    /// <summary>Gets the physical index registrations at capture time.</summary>
    IReadOnlyList<BTreeIndexRegistration> IndexRegistrations { get; }
}
