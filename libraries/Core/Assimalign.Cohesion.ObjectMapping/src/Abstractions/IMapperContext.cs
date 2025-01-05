using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Extensions.Mapping;

/// <summary>
/// 
/// </summary>
public interface IMapperContext
{
    /// <summary>
    /// The instance of the source currently being mapped.
    /// </summary>
    object Source { get; }
    /// <summary>
    /// The instance of the target currently being mapped.
    /// </summary>
    object Target { get; }
    /// <summary>
    /// Specifies whether a target member should be 
    /// ignored when writing nulls or defaults from source member.
    /// <remarks>
    ///     <b>The default is 'Never'</b>
    /// </remarks>
    /// </summary>
    MapperIgnoreHandling IgnoreHandling { get; }
    /// <summary>
    /// Specifies whether an enumerable member should be overridden or 
    /// merged together
    /// </summary>
    MapperCollectionHandling CollectionHandling { get; }
    /// <summary>
    /// The collection of profiles encapsulated by the mapper.
    /// </summary>
    /// <remarks>
    /// This should be passed by reference from the mapper when 
    /// the <see cref="IMapper.Map(object, object, Type, Type)"/> is called
    /// </remarks>
    IEnumerable<IMapperProfile> Profiles { get; }
}