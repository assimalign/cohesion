using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.DevScripts;


public class CohesionDotnetSolutionGroupingInput
{
    public string? Folder { get; set; }


    public string[] Paths
    {
        get => field;
        set
        {
            var list = new List<string>();

            foreach (var item in value)
            {
                list.Add("/" + item.Replace("\\", "/").Trim('/') + "/");
            }

            field = list.ToArray(); ;
        }
    }
}
