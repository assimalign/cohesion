using System;
using System.Globalization;

using Assimalign.Cohesion.ResourceGenerationCheck;

// Pin both the manifest lookup and culture-sensitive formatting in managed and native builds.
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
try
{
    throw new InvalidOperationException(string.Format(
        CultureInfo.CurrentCulture, ErrorMessages.OperationFailed, "probe", 12.5));
}
catch (InvalidOperationException exception)
{
    if (exception.Message != "Operation probe failed at 12,5.")
    {
        return 1;
    }
}

Console.WriteLine("Generated resource lookup and culture formatting passed.");
return 0;
