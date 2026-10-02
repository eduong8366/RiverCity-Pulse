namespace Sac311.Domain;

/// <summary>A cleaned value plus the data-quality flags raised while cleaning it.</summary>
public readonly record struct Cleaned<T>(T Value, DqFlags Flags);
