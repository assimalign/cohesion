using System;
using Assimalign.Cohesion.FileSystem;

var fileSystem = new PhysicalFileSystem(new PhysicalFileSystemOptions()
{
    Drive = "C"
});

var directory = fileSystem.GetDirectory("C:/users/chase");

foreach (var entry in directory.EnumerateFileSystemInfo())
{
    Console.WriteLine(entry.Path);
}

