using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Numbers inside names, as Alexa hears and writes them. An artist written "U2" or
/// "M83" is spoken "u deux" / "m quatre-vingt-trois" (fr), "u two" / "m eighty three"
/// (en), "u due" / "m ottantatré" (it), and Alexa's transcription keeps the words, so
/// neither the catalog match nor the library search sees the digits. Two directions:
/// <see cref="DigitSynonyms"/> spells a name's digits out for the catalog and dynamic
/// entity synonyms, <see cref="QueryVariants"/> turns a spoken query's number words
/// back into digits for the search.
/// </summary>
/// <remarks>
/// Numbers 0 to 999 in French, English and Italian; every other locale gets the
/// English words plus the letter/digit split. Matching is accent-, case- and
/// hyphen-insensitive ("quatre-vingt-trois", "quatre vingt trois" and "Quatre Vingt
/// Trois" all read 83).
/// </remarks>
public static class SpokenNumbers
{
    private const int MaxNumber = 999;
    private const int MaxWindow = 5;

    private static readonly string[] FrUnits = { "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf", "dix", "onze", "douze", "treize", "quatorze", "quinze", "seize", "dix-sept", "dix-huit", "dix-neuf" };
    private static readonly string[] EnUnits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    private static readonly string[] EnTens = { string.Empty, string.Empty, "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
    private static readonly string[] ItUnits = { "zero", "uno", "due", "tre", "quattro", "cinque", "sei", "sette", "otto", "nove", "dieci", "undici", "dodici", "tredici", "quattordici", "quindici", "sedici", "diciassette", "diciotto", "diciannove" };
    private static readonly string[] ItTens = { string.Empty, string.Empty, "venti", "trenta", "quaranta", "cinquanta", "sessanta", "settanta", "ottanta", "novanta" };

    private static readonly Regex DigitRun = new("\\d+", RegexOptions.CultureInvariant);
    private static readonly Regex LetterDigitBoundary = new("(?<=\\p{L})(?=\\d)|(?<=\\d)(?=\\p{L})", RegexOptions.CultureInvariant);
    private static readonly Regex Separators = new("[\\s\\-]+", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, Dictionary<string, int>> ReverseByLanguage = new(StringComparer.Ordinal);
    private static readonly object ReverseLock = new();

    /// <summary>
    /// Whether a language has its own number words here (others fall back to English).
    /// </summary>
    /// <param name="language">The two-letter language code.</param>
    /// <returns>True for fr, en and it.</returns>
    public static bool IsSupported(string? language) => language is "fr" or "en" or "it";

    /// <summary>
    /// Spells a number in a language (0 to 999).
    /// </summary>
    /// <param name="n">The number.</param>
    /// <param name="language">The two-letter language code (fr, en, it; anything else spells English).</param>
    /// <returns>The words, hyphenated as the language writes them.</returns>
    public static string Spell(int n, string? language)
    {
        if (n < 0 || n > MaxNumber)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        return language switch
        {
            "fr" => SpellFrench(n),
            "it" => SpellItalian(n),
            _ => SpellEnglish(n),
        };
    }

    /// <summary>
    /// Replaces the number words of a spoken query by digits: "u deux" gives "u 2",
    /// "m quatre vingt trois" gives "m 83". Unchanged text comes back as is.
    /// </summary>
    /// <param name="text">The spoken text.</param>
    /// <param name="language">The two-letter language code.</param>
    /// <returns>The text with digits.</returns>
    public static string ToDigits(string? text, string? language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        // Every supported language at once: the French ASR wrote U2 as "u. two"
        // (device log, 2026-11-01), an English number word inside a French request.
        // No number word means different values in two of the three languages, so
        // the union is safe. The language only chooses the spelled form (Spell).
        Dictionary<string, int> reverse = ReverseFor("*");
        string[] tokens = Separators.Split(text.Trim());
        var output = new List<string>(tokens.Length);
        int i = 0;
        while (i < tokens.Length)
        {
            bool matched = false;
            for (int window = Math.Min(MaxWindow, tokens.Length - i); window >= 1; window--)
            {
                string key = Fold(string.Join(' ', tokens, i, window));
                if (reverse.TryGetValue(key, out int value))
                {
                    output.Add(value.ToString(CultureInfo.InvariantCulture));
                    i += window;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                output.Add(tokens[i]);
                i++;
            }
        }

        return string.Join(' ', output);
    }

    /// <summary>
    /// The search queries to try when a spoken query carries number words: the digit
    /// form ("u 2") and its compact form ("u2"), in that order, both different from the
    /// original. Empty when the query has no number words.
    /// </summary>
    /// <param name="query">The spoken query.</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The variants.</returns>
    public static IReadOnlyList<string> QueryVariants(string? query, string? locale)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        string language = LocalePrefix.Of(locale ?? string.Empty);
        // Alexa writes a spelled letter with a period ("m. quatre vingt trois",
        // device-observed for M83): the period is not part of any library name.
        string original = Regex.Replace(query.Trim(), "\\b(\\p{L}{1,2})\\.(?=\\s|$)", "$1", RegexOptions.CultureInvariant);
        string spaced = ToDigits(original, language);
        var variants = new List<string>();
        if (!string.Equals(spaced, original, StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(spaced);
        }

        // Alexa also writes "m 83" with the digits already there: the compact "m83" is
        // how such names are spelled in a library, so it is a variant on its own.
        string compact = Compact(spaced);
        if (!string.Equals(compact, spaced, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(compact, original, StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(compact);
        }

        return variants;
    }

    /// <summary>
    /// The synonyms a name with digits needs so Alexa recognises it spoken: the
    /// letter/digit split ("U 2", "M 83") and the digits spelled out in the locale's
    /// language ("U deux", "M quatre-vingt-trois"). Empty for names without digits.
    /// </summary>
    /// <param name="name">The catalog name.</param>
    /// <param name="locale">The catalog locale.</param>
    /// <returns>The synonyms, distinct and different from the name.</returns>
    public static IReadOnlyList<string> DigitSynonyms(string? name, string? locale)
    {
        if (string.IsNullOrWhiteSpace(name) || !DigitRun.IsMatch(name))
        {
            return Array.Empty<string>();
        }

        string language = LocalePrefix.Of(locale ?? string.Empty);
        string spaced = Separators.Replace(LetterDigitBoundary.Replace(name, " "), " ").Trim();
        string spelled = DigitRun.Replace(spaced, m => int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n <= MaxNumber ? Spell(n, IsSupported(language) ? language : "en") : m.Value);

        var results = new List<string>();
        foreach (string candidate in new[] { spaced, spelled })
        {
            if (!string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)
                && !results.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    private static string Compact(string spaced)
    {
        // Join a lone letter or word to an adjacent number ("u 2" -> "u2", "m 83" -> "m83"),
        // the way band names are written; "maroon 5" stays as both forms are tried.
        return Regex.Replace(spaced, "(?<=^|\\s)(\\p{L}{1,2})\\s+(\\d+)(?=\\s|$)|(?<=^|\\s)(\\d+)\\s+(\\p{L}{1,2})(?=\\s|$)", m => m.Value.Replace(" ", string.Empty, StringComparison.Ordinal), RegexOptions.CultureInvariant);
    }

    private static Dictionary<string, int> ReverseFor(string language)
    {
        lock (ReverseLock)
        {
            if (ReverseByLanguage.TryGetValue(language, out Dictionary<string, int>? cached))
            {
                return cached;
            }

            var reverse = new Dictionary<string, int>(StringComparer.Ordinal);
            if (language == "*")
            {
                foreach (string each in new[] { "fr", "en", "it" })
                {
                    foreach (KeyValuePair<string, int> pair in ReverseFor(each))
                    {
                        reverse.TryAdd(pair.Key, pair.Value);
                    }
                }

                ReverseByLanguage[language] = reverse;
                return reverse;
            }

            for (int n = 0; n <= MaxNumber; n++)
            {
                reverse.TryAdd(Fold(Spell(n, language)), n);
            }

            if (language == "it")
            {
                // Spoken Italian often separates tens and units ("ottanta tre").
                for (int n = 21; n <= 99; n++)
                {
                    if (n % 10 != 0)
                    {
                        reverse.TryAdd(Fold(ItTens[n / 10] + " " + ItUnits[n % 10]), n);
                    }
                }
            }

            if (language == "fr")
            {
                // "quatre vingts" / "deux cents" with the plural the ASR sometimes writes.
                reverse.TryAdd("quatre vingts", 80);
                for (int h = 2; h <= 9; h++)
                {
                    reverse.TryAdd(Fold(FrUnits[h] + " cents"), h * 100);
                }
            }

            ReverseByLanguage[language] = reverse;
            return reverse;
        }
    }

    private static string Fold(string text)
    {
        string lowered = Separators.Replace(text, " ").Trim().ToLowerInvariant();
        string decomposed = lowered.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string SpellEnglish(int n)
    {
        if (n < 20)
        {
            return EnUnits[n];
        }

        if (n < 100)
        {
            return n % 10 == 0 ? EnTens[n / 10] : EnTens[n / 10] + "-" + EnUnits[n % 10];
        }

        string hundreds = EnUnits[n / 100] + " hundred";
        return n % 100 == 0 ? hundreds : hundreds + " " + SpellEnglish(n % 100);
    }

    private static string SpellFrench(int n)
    {
        if (n < 20)
        {
            return FrUnits[n];
        }

        if (n < 100)
        {
            int tens = n / 10;
            int units = n % 10;
            switch (tens)
            {
                case 7:
                case 9:
                {
                    // 70-79 and 90-99 ride on 60 and 80 plus 10-19.
                    string baseWord = tens == 7 ? "soixante" : "quatre-vingt";
                    int rest = n - (tens == 7 ? 60 : 80);
                    return n == 71 ? "soixante et onze" : baseWord + "-" + FrUnits[rest];
                }

                case 8:
                    return units == 0 ? "quatre-vingts" : "quatre-vingt-" + FrUnits[units];
                default:
                {
                    string tensWord = tens switch { 2 => "vingt", 3 => "trente", 4 => "quarante", 5 => "cinquante", _ => "soixante" };
                    return units == 0 ? tensWord : units == 1 ? tensWord + " et un" : tensWord + "-" + FrUnits[units];
                }
            }
        }

        int h = n / 100;
        string hundreds = h == 1 ? "cent" : FrUnits[h] + " cent";
        if (n % 100 == 0)
        {
            return h == 1 ? "cent" : hundreds + "s";
        }

        return hundreds + " " + SpellFrench(n % 100);
    }

    private static string SpellItalian(int n)
    {
        if (n < 20)
        {
            return ItUnits[n];
        }

        if (n < 100)
        {
            int tens = n / 10;
            int units = n % 10;
            string tensWord = ItTens[tens];
            if (units == 0)
            {
                return tensWord;
            }

            // The tens word drops its final vowel before uno and otto; tre takes an accent.
            string stem = units is 1 or 8 ? tensWord.Substring(0, tensWord.Length - 1) : tensWord;
            return stem + (units == 3 ? "tré" : ItUnits[units]);
        }

        int h = n / 100;
        string hundreds = h == 1 ? "cento" : ItUnits[h] + "cento";
        if (n % 100 == 0)
        {
            return hundreds;
        }

        // "cento" drops its final o before otto/ottanta ("centotto", "centottantadue").
        string rest = SpellItalian(n % 100);
        return (rest[0] == 'o' ? hundreds.Substring(0, hundreds.Length - 1) : hundreds) + rest;
    }
}
