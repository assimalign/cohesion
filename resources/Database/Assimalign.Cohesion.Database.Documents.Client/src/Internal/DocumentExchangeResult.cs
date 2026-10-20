using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Internal;

internal sealed record DocumentExchangeResult(DocumentResultSet Result, Document? Document, IReadOnlyList<string> Collections);
