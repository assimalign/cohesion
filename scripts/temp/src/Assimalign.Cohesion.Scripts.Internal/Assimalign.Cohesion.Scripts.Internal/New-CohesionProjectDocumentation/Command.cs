using Assimalign.Cohesion.Scripts.Internal;
using System;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Assimalign.Cohesion.Scripts;

[Cmdlet(VerbsDiagnostic.Test, "SampleCmdlet")]
[OutputType(typeof(CohesionProjectDocumentationOutput))]
public class NewCohesionProjectDocumentationCmdlet : PSCmdlet
{

    [Parameter(
        Mandatory = true, 
        Position = 0)]
    public string? ProjectPath { get; set; }

    // This method gets called once for each cmdlet in the pipeline when the pipeline starts executing
    protected override void BeginProcessing()
    {
        WriteVerbose("Begin!");
    }


    // This method will be called for each input received from the pipeline to this cmdlet; if no input is received, this method is not called
    protected override void ProcessRecord()
    {
        WriteObject(new CohesionProjectDocumentationOutput
        {
           
        });
    }

    // This method will be called once at the end of pipeline execution; if no input is received, this method is not called
    protected override void EndProcessing()
    {
        WriteVerbose("End!");
    }
}
