using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Execution.Storage
{
    public interface IPanopticDbStorageResource : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// 
        /// </summary>
        IPanopticDbStorageResourceHeader Header { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        IPanopticDbStorageIndexIterator CreateIndexIterator();

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        IPanopticDbStorageSegmentIterator CreateSegmentIterator();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        Task LoadAsync(PanopticDbStorageContext context);
    }
}
