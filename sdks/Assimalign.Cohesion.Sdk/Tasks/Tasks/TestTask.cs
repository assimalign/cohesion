using Microsoft.Build;
using Microsoft.Build.Definition;
using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;

namespace Assimalign.Cohesion.Sdk.Tasks
{
    public class TestTask : Task
    {
        public TestTask()
        {
            
        }

        public override bool Execute()
        {
            Log.LogError("");
            throw new System.NotImplementedException();
        }
    }
}
