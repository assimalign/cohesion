using Assimalign.PanopticDb.Execution.Storage.ValueTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Execution.Storage.Document;


public class PanopticDbDocumentCollectionHeader
{
    /// <summary>
    /// The position of constraints for the collection
    /// </summary>
    public long ConstraintsPosition { get; set; }
     
    /// <summary>
    /// The position of the index segment
    /// </summary>
    public long IndexPosition { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public ImmutableName Partion { get; set; }
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    
    public T Get<T>();
}