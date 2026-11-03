using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tcp.Tests;

public sealed class DatabaseEndpointResourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Tcp] - Shared endpoint binder resources retain identical CK07101 text (#1790)")]
    public void EndpointHostUnsupported_BothConsumers_ShouldHaveIdenticalResource()
    {
        // Arrange
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Assimalign.Cohesion.slnx")))
        {
            directory = directory.Parent;
        }
        directory.ShouldNotBeNull();
        string area = Path.Combine(directory.FullName, "resources", "Database");

        // Act
        string root = Read("Assimalign.Cohesion.Database");
        string tcp = Read("Assimalign.Cohesion.Database.Tcp");

        // Assert
        tcp.ShouldBe(root);
        root.ShouldStartWith("CK07101:", Case.Sensitive);

        string Read(string project) => XDocument.Load(Path.Combine(area, project, "src", "Properties", "ErrorMessages.resx"))
            .Root!.Elements("data").Single(entry => (string?)entry.Attribute("name") == "EndpointHostUnsupported")
            .Element("value")!.Value;
    }
}
