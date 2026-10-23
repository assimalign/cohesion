using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>Invocation-local state; custom actions share one lazily created public context.</summary>
[global::System.Diagnostics.DebuggerDisplay("MapperExecution")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal struct MapperExecution
{
    private MapperContext? _context;

    public MapperExecution(object target, object source, List<IMapperProfile> profiles,
        MapperIgnoreHandling ignoreHandling, MapperCollectionHandling collectionHandling)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        Target = target;
        Source = source;
        Profiles = profiles;
        IgnoreHandling = ignoreHandling;
        CollectionHandling = collectionHandling;
    }

    public object Target { get; }
    public object Source { get; }
    public List<IMapperProfile> Profiles { get; }
    public MapperIgnoreHandling IgnoreHandling { get; }
    public MapperCollectionHandling CollectionHandling { get; }

    public MapperContext GetContext() => _context ??= new MapperContext(Target, Source)
    {
        Profiles = Profiles,
        IgnoreHandling = IgnoreHandling,
        CollectionHandling = CollectionHandling
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(IMapperProfile profile)
    {
        if (profile is IMapperProfileExecutor executor)
        {
            executor.Invoke(ref this);
        }
        else
        {
            foreach (IMapperAction action in profile.MapActions)
            {
                action.Invoke(GetContext());
            }
        }
    }

    private sealed class DebugView
    {
        private readonly MapperExecution _target;

        public DebugView(MapperExecution value)
        {
            _target = value;
        }

        public object Target => _target.Target;
        public object Source => _target.Source;
        public List<IMapperProfile> Profiles => _target.Profiles;
        public MapperIgnoreHandling IgnoreHandling => _target.IgnoreHandling;
        public MapperCollectionHandling CollectionHandling => _target.CollectionHandling;
    }
}
