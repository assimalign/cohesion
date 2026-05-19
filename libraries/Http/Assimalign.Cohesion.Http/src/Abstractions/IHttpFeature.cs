namespace Assimalign.Cohesion.Http;

/*
 Right now keeping the interface with name only, but this is the root contract for all HTTP features. Http Features represents 
specific Http related capabilities that can be attached to the HttpContext. They are used to extend the functionality of the HTTP pipeline a
nd provide additional services or information related to the HTTP request and response. Features are separate from actuall application services, and are 
designed to be used by middleware and other components in the HTTP pipeline to access specific capabilities or information 
about the request and response. Examples of features include authentication, session management, connection lifetime management, etc.
 */


/// <summary>
/// Represents a 
/// </summary>
public interface IHttpFeature
{
    /// <summary>
    /// The name of the feature, which is used to identify the feature when it is attached 
    /// to the HttpContext. The name should be unique across all features and should be
    /// descriptive of the feature's purpose.
    /// </summary>
    string Name { get; }
}