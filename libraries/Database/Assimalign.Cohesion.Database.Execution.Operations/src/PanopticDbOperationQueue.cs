using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Operations
{
    public class Cohesion.DatabaseOperationQueue
    {
        private readonly Queue<IPanoptisDbOperation> operationsQueue;


        public Cohesion.DatabaseOperationQueue()
        {

        }



        public Task ProcessAsync(CancellationToken cancellationToken)
        {

        }

    }
}
