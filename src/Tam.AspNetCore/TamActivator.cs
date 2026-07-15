using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Tam.AspNetCore;

/// <summary>
/// Constructs plugin handler classes (gates, effect handlers, parked work) with constructor
/// injection from the scope it was resolved in. Handlers need no DI registration — the ctor
/// signature IS the dependency declaration. Factories are cached per type: a wildcard gate is
/// instantiated on every operation, so this sits on the pipeline's hot path.
/// </summary>
public sealed class TamActivator(IServiceProvider services) : ITamActivator
{
    private static readonly ConcurrentDictionary<Type, ObjectFactory> Factories = new();

    public object Create(Type handlerType) =>
        Factories.GetOrAdd(handlerType, t => ActivatorUtilities.CreateFactory(t, Type.EmptyTypes))
            .Invoke(services, null);
}
