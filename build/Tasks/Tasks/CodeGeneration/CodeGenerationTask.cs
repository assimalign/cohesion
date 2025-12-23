using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Build.Tasks;

public abstract class CodeGenerationTask: Microsoft.Build.Utilities.Task
{

    /*
        All Code gen should be placed in a single output directory that is not tracked by source control
     */

    [Required]
    public string? CodeGenOutputPath { get; set; }
}
