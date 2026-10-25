# Logical resource groups

Application composition can place resources in named logical containers:

```csharp
IApplicationBuilder builder = Gateway.CreateBuilder(args);

builder.AddResourceGroup("frontend", group =>
{
    group.AddWeb(Manifests.PublicApi);
    group.AddWeb(Manifests.AdminPortal);
});

builder.AddResourceGroup("background", group =>
{
    group.AddResource(Manifests.Worker);
});

await builder.Build().RunAsync();
```

The callback is an `IApplicationBuilder`, so the existing resource-area extension members need
no group-specific overloads. Groups are flat and case-sensitive. A group name contains one to
128 ASCII letters, digits, periods, underscores, or hyphens, excluding `.` and `..`. Declare each
group once; an empty group is preserved. Resources added outside a callback remain ungrouped.
Resource names are unique across the application, and dependencies may cross group boundaries.

`IApplicationModel.ResourceGroups` exposes immutable names and member resource names.
`IApplicationResourceDescriptor.ResourceGroup` and `ResourcePlan.ResourceGroup` expose each
resource's optional membership. Describe output and exports preserve both the group catalog
and membership:

```json
{
  "resourceGroups": [
    { "name": "frontend", "resources": ["public-api", "admin-portal"] },
    { "name": "background", "resources": ["worker"] }
  ],
  "resources": [
    {
      "name": "public-api",
      "resourceGroup": "frontend",
      "plan": { "resourceGroup": "frontend" }
    }
  ]
}
```

The fragment omits other required resource and plan fields. Import checks that every member
exists, belongs to at most one group, and agrees with its descriptor and plan. An older document
with no groups remains ungrouped. Producer `resource.json` does not declare groups because the
application chooses this logical organization when it composes resources.

Groups affect how resources can be presented. They create no infrastructure namespace, RBAC
scope, ownership boundary, deployment policy, lifecycle ordering, or delete cascade. They do not
change resource manifest hashes or the workload, service, storage, or environment specifications
handed to a platform gateway.
