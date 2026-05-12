using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Language
{
    /// <summary>
    /// An abstract analyzer for implementing custom analysis on parsed queries.
    /// </summary>
    public abstract class QueryAnalyzer
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task AnalyzeAsync(QueryAnalyzerContext context, CancellationToken cancellationToken = default);
    }
}
