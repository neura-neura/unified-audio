using System.Globalization;
using UnifiedAudio.Models;

namespace UnifiedAudio.Helpers;

public static class Loc
{
    public static AppLanguage Language { get; set; } = AppLanguage.System;

    public static AppLanguage EffectiveLanguage => Language == AppLanguage.System
        ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "es" => AppLanguage.Spanish,
            "zh" => AppLanguage.SimplifiedChinese,
            _ => AppLanguage.English
        }
        : Language;

    public static string Get(string key)
    {
        var selected = EffectiveLanguage;
        var table = selected switch
        {
            AppLanguage.Spanish => StringCatalog.Spanish,
            AppLanguage.SimplifiedChinese => StringCatalog.SimplifiedChinese,
            _ => StringCatalog.English
        };
        if (table.TryGetValue(key, out var value)) return value;
        return StringCatalog.English.TryGetValue(key, out value) ? value : key;
    }

    public static string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(Get(key), args);
        }
        catch
        {
            return Get(key);
        }
    }
}
