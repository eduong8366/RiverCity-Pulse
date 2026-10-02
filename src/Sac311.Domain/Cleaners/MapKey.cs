namespace Sac311.Domain.Cleaners;

public static class MapKey
{
    /// <summary>
    /// Lowercase ASCII letters and digits only, so spelling variants share a key ("GoogleAI" and "Google AI" are
    /// both <c>googleai</c>). Returns null when nothing is left. The ref.*_map seeds are keyed by this.
    /// </summary>
    public static string? For(string? value)
    {
        if (value is null)
        {
            return null;
        }

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var n = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[n++] = char.ToLowerInvariant(c);
            }
        }

        return n == 0 ? null : new string(buffer[..n]);
    }
}
