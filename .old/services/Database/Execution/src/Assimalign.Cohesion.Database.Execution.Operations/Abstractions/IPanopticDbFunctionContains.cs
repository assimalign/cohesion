using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Operations.Abstractions
{
    public interface ICohesion.DatabaseFunctionContains<in TEnumerable>
        where TEnumerable : IEnumerable
    {


        public bool Contains(TEnumerable item);
    }
}
