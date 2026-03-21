using System.Collections.Concurrent;

namespace Scrinia.Core.Resilience;

/// <summary>
/// Global registry of named circuit breakers. Providers register their CB instances
/// so health endpoints can report state without tight coupling.
/// </summary>
public static class CircuitBreakerRegistry
{
    private static readonly ConcurrentDictionary<string, CircuitBreaker> Breakers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a circuit breaker by name. Overwrites if name already exists.</summary>
    public static void Register(string name, CircuitBreaker cb) => Breakers[name] = cb;

    /// <summary>Try to get a circuit breaker by name.</summary>
    public static bool TryGet(string name, out CircuitBreaker? cb) => Breakers.TryGetValue(name, out cb);

    /// <summary>Returns all registered circuit breakers.</summary>
    public static IReadOnlyDictionary<string, CircuitBreaker> GetAll() => Breakers;

    /// <summary>Remove a circuit breaker by name. Returns true if removed.</summary>
    public static bool Remove(string name) => Breakers.TryRemove(name, out _);

    /// <summary>Clear all registered circuit breakers. For testing only.</summary>
    internal static void Clear() => Breakers.Clear();
}
