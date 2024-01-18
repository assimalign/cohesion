using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Execution.Operations.Abstractions
{
    public interface IPanopticDbFunctionContains<in TEnumerable>
        where TEnumerable : IEnumerable
    {


        public bool Contains(TEnumerable item);
    }
}
