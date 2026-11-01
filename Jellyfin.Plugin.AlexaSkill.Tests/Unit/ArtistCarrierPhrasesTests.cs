using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Artist carriers read from the locale models: "la musique de M83" in a free-text song
/// slot is an artist request the catalog gate misrouted.
/// </summary>
public class ArtistCarrierPhrasesTests
{
    [Fact]
    public void ExtractCarriers_KeepsMultiWordCarriersWithoutTheVerb()
    {
        string[] carriers = ArtistCarrierPhrases.ExtractCarriers(new[]
        {
            "Lis la musique de {musician}",
            "Mets les chansons de {musician}",
            "Mets {musician}",
            "Mets les {musician}",
            "Lis {song} de {musician}",
            "Lis la musique de {musician} s'il te plaît",
        });

        Assert.Equal(new[] { "les chansons de", "la musique de" }, carriers);
    }

    [Theory]
    [InlineData("la musique de M83", "fr-FR", "M83")]
    [InlineData("Les chansons de U2", "fr-FR", "U2")]
    [InlineData("la musique des Beatles", "fr-FR", "Beatles")]
    [InlineData("songs by M83", "en-US", "M83")]
    public void TryStrip_RecoversTheArtistFromTheLocaleModel(string slotText, string locale, string expected)
    {
        Assert.True(ArtistCarrierPhrases.TryStrip(slotText, locale, out string artist));
        Assert.Equal(expected, artist);
    }

    [Theory]
    [InlineData("midnight city", "fr-FR")]
    [InlineData("la musique de", "fr-FR")]
    [InlineData("", "fr-FR")]
    [InlineData("la musique de M83", "")]
    public void TryStrip_LeavesPlainTitlesAlone(string slotText, string locale)
    {
        Assert.False(ArtistCarrierPhrases.TryStrip(slotText, locale, out _));
    }
}
