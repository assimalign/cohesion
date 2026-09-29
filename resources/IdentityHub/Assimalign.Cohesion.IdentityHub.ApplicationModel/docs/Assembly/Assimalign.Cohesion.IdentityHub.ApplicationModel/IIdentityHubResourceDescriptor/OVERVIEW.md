# IIdentityHubResourceDescriptor

Extends IApplicationResourceDescriptor and IResourceCommandDescriptor. Resource exposes the
typed IdentityHubResource; Commands lists desired commands; AddCommand attaches a command.
Both DependsOn overloads return IIdentityHubResourceDescriptor, retaining AddAudience/AddClient
through dependency chaining. AddIdentityHub constructs the internal wrapper; existing assignments
to IApplicationResourceDescriptor remain valid. The interface owns no runtime host or service state.
