using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Storage;

/// <summary>
/// Specifies whether the Data Page is in 
/// </summary>
public enum PageOrientation
{
    Unknown = -1,
    ColumnStore,
    RowStore
}
