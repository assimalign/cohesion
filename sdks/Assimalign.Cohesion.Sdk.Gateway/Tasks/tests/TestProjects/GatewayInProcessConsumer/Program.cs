using System;

using Assimalign.Cohesion.ApplicationModel.Gateway;

// The gateway references a Sdk.Web resource project, as an in-process gateway does, and calls into it.
// Building proves the gateway accepts the self-contained resource reference (NETSDK1151); running
// proves the resource's App frameworks load in the gateway's process.
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("smoke");
builder.AddResource("Web", "smoke-api");
Console.WriteLine($"gateway={builder.Name.Value};resource={GatewayWebResource.Program.Describe()}");
