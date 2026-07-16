using System.Collections.Concurrent;
using System.Reflection;

namespace Tam.AspNetCore;

/// <summary>
/// Per-process memos for the pipeline's hot-path reflection (review-round-4 #5): the model is
/// compiled once, so its MethodInfos/PropertyInfos are a small closed set — resolve each once,
/// not per request. MethodInvoker (BCL) caches argument marshalling, cutting the dominant cost
/// of MethodInfo.Invoke without hand-rolled expression compilation.
/// </summary>
internal static class ReflectionCache
{
    private static readonly ConcurrentDictionary<MethodInfo, MethodInvoker> Invokers = new();
    private static readonly ConcurrentDictionary<MethodInfo, ParameterInfo[]> ParameterSets = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo> Properties = new();

    public static MethodInvoker Invoker(MethodInfo method) =>
        Invokers.GetOrAdd(method, MethodInvoker.Create);

    public static ParameterInfo[] Parameters(MethodInfo method) =>
        ParameterSets.GetOrAdd(method, m => m.GetParameters());

    public static PropertyInfo Property(Type type, string name) =>
        Properties.GetOrAdd((type, name), key => key.Type.GetProperty(key.Name)
            ?? throw new InvalidOperationException($"{key.Type.Name} has no property '{key.Name}'."));
}
