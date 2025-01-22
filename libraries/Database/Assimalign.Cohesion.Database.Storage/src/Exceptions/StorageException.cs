using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

public abstract class StorageException : Exception
{
    public StorageException(string message) : base(message)
    {
        
    }
    public StorageException(string message, Exception inner) : base(message, inner)
    {
        
    }
}
