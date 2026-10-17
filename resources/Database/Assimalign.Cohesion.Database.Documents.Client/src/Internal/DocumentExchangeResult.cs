using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Documents.Client.Internal;

internal sealed record DocumentExchangeResult(DocumentResultSet Result, Document? Document, IReadOnlyList<string> Collections);
