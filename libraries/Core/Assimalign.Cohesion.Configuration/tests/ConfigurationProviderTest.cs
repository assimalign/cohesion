using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationProviderTest
{
    [Fact]
    public void Test()
    {
        var provider = new MockConfigurationProvider(entries =>
        {
            entries["Azure:Identity"] = "value1";
            entries["Azure:Identity:Provider"] = "value2";
            entries["Azure:Identity:RedirectUrl"] = "value3";


            entries["Twilio:Url"] = "value3";
            entries["Twilio:Token"] = "value3";
            entries["Twilio:Password"] = "value3";

            entries["Version"] = "v1.0.0";
            entries["ReleaseDate"] = "2026-02-25";
        });

        provider.Load();
    }
}
