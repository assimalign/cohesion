using System.Collections.Generic;

using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog.Internal;

/// <summary>
/// One atomic capture of the catalog's format and index registrations.
/// </summary>
internal sealed record KeyValueCatalogSnapshot(
    int EntrySpaceFormatVersion,
    IReadOnlyList<BTreeIndexRegistration> IndexRegistrations) : IKeyValueCatalogSnapshot;
