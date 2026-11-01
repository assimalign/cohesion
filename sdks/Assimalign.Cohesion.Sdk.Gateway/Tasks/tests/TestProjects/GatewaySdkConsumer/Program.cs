using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;

// The gateway declares its model in code; the SDK generates nothing. Running the model needs a
// platform's infrastructure, which this smoke does not reference, so it reports the declaration.
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("smoke");
IApplicationResourceDefinition api = builder.AddResource("Web", "smoke-api");
Console.WriteLine($"gateway={builder.Name.Value};resource={api.Provider.Value}/{api.Name.Value}");
