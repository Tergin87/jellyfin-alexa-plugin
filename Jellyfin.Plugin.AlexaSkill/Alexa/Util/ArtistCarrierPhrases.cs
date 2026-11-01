using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The words a locale puts between the verb and the artist in "play the music of X"
/// ("la musique de", "les chansons de", "songs by"), read from the locale's own
/// interaction model (the PlayArtistSongsIntent samples that end with the musician
/// slot). When Alexa routes such a sentence to a free-text slot instead (the song slot
/// receives "la musique de M83" because the artist is not in the catalog), stripping the
/// carrier recovers the artist the user actually named.
/// </summary>
public static class ArtistCarrierPhrases
{
    private const string IntentName = "PlayArtistSongsIntent";
    private const string SlotMarker = "{musician}";
    private static readonly ConcurrentDictionary<string, string[]> CarriersByLocale = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Strips a leading artist carrier of the locale ("la musique de M83" gives "M83").
    /// </summary>
    /// <param name="text">The slot text.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="artist">The remainder, when a carrier was found and something follows it.</param>
    /// <returns>True when a carrier was stripped.</returns>
    public static bool TryStrip(string? text, string? locale, out string artist)
    {
        artist = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(locale))
        {
            return false;
        }

        string trimmed = text.Trim();
        foreach (string carrier in CarriersFor(locale))
        {
            if (trimmed.Length > carrier.Length + 1
                && trimmed.StartsWith(carrier, StringComparison.OrdinalIgnoreCase)
                && trimmed[carrier.Length] == ' ')
            {
                artist = trimmed.Substring(carrier.Length + 1).Trim();
                return artist.Length > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// The carriers of a locale, longest first: every PlayArtistSongsIntent sample that
    /// ends with the musician slot, minus its leading verb, when at least two words remain
    /// ("Mets les {musician}" yields the one-word "les", too generic, and is skipped).
    /// </summary>
    /// <param name="locale">The locale.</param>
    /// <returns>The carriers.</returns>
    public static IReadOnlyList<string> CarriersFor(string locale)
        => CarriersByLocale.GetOrAdd(locale, l => ExtractCarriers(LoadSamples(l)));

    /// <summary>
    /// Derives the carriers from sample utterances (exposed for tests).
    /// </summary>
    /// <param name="samples">The intent's samples.</param>
    /// <returns>The carriers, longest first.</returns>
    internal static string[] ExtractCarriers(IEnumerable<string> samples)
    {
        var carriers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sample in samples)
        {
            string s = sample.Trim();
            // Only samples whose single slot is the trailing musician ("Lis {song} de {musician}" is skipped).
            if (!s.EndsWith(SlotMarker, StringComparison.Ordinal) || s.IndexOf('{', StringComparison.Ordinal) != s.Length - SlotMarker.Length)
            {
                continue;
            }

            string[] words = s.Substring(0, s.Length - SlotMarker.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 3)
            {
                // verb + at least two carrier words ("Lis la musique de").
                continue;
            }

            carriers.Add(string.Join(' ', words.Skip(1)).ToLowerInvariant());
        }

        return carriers.OrderByDescending(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> LoadSamples(string locale)
    {
        try
        {
            Tuple<string, string>? model = Jellyfin.Plugin.AlexaSkill.Util.GetLocalInteractionModels().FirstOrDefault(m => string.Equals(m.Item1, locale, StringComparison.OrdinalIgnoreCase));
            if (model == null)
            {
                return Array.Empty<string>();
            }

            using Stream? resource = typeof(Jellyfin.Plugin.AlexaSkill.Util).Assembly.GetManifestResourceStream(model.Item2);
            if (resource == null)
            {
                return Array.Empty<string>();
            }

            using JsonDocument document = JsonDocument.Parse(resource);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("interactionModel", out JsonElement wrapped))
            {
                root = wrapped;
            }

            if (!root.TryGetProperty("languageModel", out JsonElement languageModel) || !languageModel.TryGetProperty("intents", out JsonElement intents))
            {
                return Array.Empty<string>();
            }

            foreach (JsonElement intent in intents.EnumerateArray())
            {
                if (intent.TryGetProperty("name", out JsonElement name) && name.GetString() == IntentName && intent.TryGetProperty("samples", out JsonElement samples))
                {
                    return samples.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // A malformed embedded model only disables this recovery.
        }

        return Array.Empty<string>();
    }
}
