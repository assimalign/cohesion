using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Storage
{
    public interface ICohesion.DatabaseStorageResource : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// 
        /// </summary>
        ICohesion.DatabaseStorageResourceHeader Header { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        ICohesion.DatabaseStorageIndexIterator CreateIndexIterator();

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        ICohesion.DatabaseStorageSegmentIterator CreateSegmentIterator();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        Task LoadAsync(Cohesion.DatabaseStorageContext context);
    }
}
