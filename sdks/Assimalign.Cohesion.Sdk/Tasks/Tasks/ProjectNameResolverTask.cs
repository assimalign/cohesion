using Microsoft.Build;
using Microsoft.Build.Definition;
using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using Microsoft.Build.Evaluation;

namespace Assimalign.Cohesion.Sdk.Tasks;

public class ProjectNameResolverTask : Task
{

    public ProjectNameResolverTask()
    {
        
    }


    [Output]
    public ITaskItem[] ProjectReferenceProvider { get; set; }


    public override bool Execute()
    {
        var item = new TaskItem("ProjectReferenceProvider");
        
        throw new System.NotImplementedException();
    }
}