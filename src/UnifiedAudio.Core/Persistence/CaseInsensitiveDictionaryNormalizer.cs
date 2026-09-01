namespace UnifiedAudio.Core.Persistence;

/// <summary>
/// Builds an OrdinalIgnoreCase dictionary without letting case-only duplicate
/// keys make normalization fail. The ordinal sort makes the retained entry
/// deterministic for every input dictionary.
/// </summary>
public static class CaseInsensitiveDictionaryNormalizer
{
    public static Dictionary<string, TValue> Normalize<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> source,
        Func<TValue, bool> includeValue,
        Func<TValue, TValue> normalizeValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(includeValue);
        ArgumentNullException.ThrowIfNull(normalizeValue);

        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && includeValue(pair.Value))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (result.ContainsKey(pair.Key))
            {
                continue;
            }

            result[pair.Key] = normalizeValue(pair.Value);
        }

        return result;
    }
}
