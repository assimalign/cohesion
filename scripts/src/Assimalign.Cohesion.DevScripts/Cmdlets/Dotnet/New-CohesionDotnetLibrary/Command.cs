using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Text;

namespace Assimalign.Cohesion.DevScripts;

[Cmdlet("New", "CohesionDotnetLibrary")]
[OutputType(typeof(CohesionDotnetLibrary))]
public class NewCohesionDotnetLibraryCmdlet : PSCmdlet
{

    [Parameter(Position = 0, Mandatory = true)]
    public string? ServiceName { get; set; }


    [Parameter(Position = 1, Mandatory = true)]
    public string? LibraryName
    {
        get => field;
        set
        {
            if (!value.StartsWith("Assimalign.Cohesion"))
            {
                throw new ArgumentException("Invalid library name. Must start with 'Assimalign.Cohesion.'.");
            }

            field = value;
        }
    }


    protected override void BeginProcessing()
    {
        base.BeginProcessing();
    }


    protected override void EndProcessing()
    {
        base.EndProcessing();
    }


    protected override void ProcessRecord()
    {
        base.ProcessRecord();
    }
}
