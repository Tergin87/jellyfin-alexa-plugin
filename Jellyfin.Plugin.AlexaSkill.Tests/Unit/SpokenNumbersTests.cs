using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Numbers inside names, both ways: digits spelled out for the catalog synonyms, and
/// spoken number words turned back into digits for the search ("U2", "M83").
/// </summary>
public class SpokenNumbersTests
{
    [Theory]
    [InlineData(2, "fr", "deux")]
    [InlineData(21, "fr", "vingt et un")]
    [InlineData(71, "fr", "soixante et onze")]
    [InlineData(80, "fr", "quatre-vingts")]
    [InlineData(83, "fr", "quatre-vingt-trois")]
    [InlineData(99, "fr", "quatre-vingt-dix-neuf")]
    [InlineData(182, "fr", "cent quatre-vingt-deux")]
    [InlineData(200, "fr", "deux cents")]
    [InlineData(2, "en", "two")]
    [InlineData(83, "en", "eighty-three")]
    [InlineData(182, "en", "one hundred eighty-two")]
    [InlineData(2, "it", "due")]
    [InlineData(21, "it", "ventuno")]
    [InlineData(83, "it", "ottantatré")]
    [InlineData(88, "it", "ottantotto")]
    [InlineData(182, "it", "centottantadue")]
    [InlineData(5, "de", "five")]
    public void Spell_WritesNumbersInEachLanguage(int n, string language, string expected)
    {
        Assert.Equal(expected, SpokenNumbers.Spell(n, language));
    }

    [Theory]
    [InlineData("u deux", "fr-FR", "u 2")]
    [InlineData("U Deux", "fr-FR", "U 2")]
    [InlineData("m quatre vingt trois", "fr-FR", "m 83")]
    [InlineData("m quatre-vingt-trois", "fr-FR", "m 83")]
    [InlineData("blink cent quatre vingt deux", "fr-FR", "blink 182")]
    [InlineData("maroon cinq", "fr-FR", "maroon 5")]
    [InlineData("u two", "en-US", "u 2")]
    [InlineData("m eighty three", "en-GB", "m 83")]
    [InlineData("sum forty-one", "en-US", "sum 41")]
    [InlineData("u due", "it-IT", "u 2")]
    [InlineData("m ottantatré", "it-IT", "m 83")]
    [InlineData("m ottanta tre", "it-IT", "m 83")]
    [InlineData("pink floyd", "fr-FR", "pink floyd")]
    [InlineData("the beatles", "en-US", "the beatles")]
    // The French ASR mixes English number words in ("u. two" for U2, device log).
    [InlineData("u two", "fr-FR", "u 2")]
    [InlineData("m eighty three", "fr-FR", "m 83")]
    [InlineData("u deux", "en-US", "u 2")]
    public void ToDigits_ReplacesNumberWords(string spoken, string locale, string expected)
    {
        Assert.Equal(expected, SpokenNumbers.ToDigits(spoken, LocalePrefix.Of(locale)));
    }

    [Fact]
    public void QueryVariants_GivesDigitAndCompactForms()
    {
        Assert.Equal(new[] { "u 2", "u2" }, SpokenNumbers.QueryVariants("u deux", "fr-FR"));
        // The ASR's rendering of a spelled letter, with its period (device log, M83).
        Assert.Equal(new[] { "m 83", "m83" }, SpokenNumbers.QueryVariants("m. quatre vingt trois", "fr-FR"));
        Assert.Equal(new[] { "u 2", "u2" }, SpokenNumbers.QueryVariants("u. two", "fr-FR"));
        Assert.Equal(new[] { "m 83", "m83" }, SpokenNumbers.QueryVariants("m quatre vingt trois", "fr-FR"));
        Assert.Equal(new[] { "maroon 5" }, SpokenNumbers.QueryVariants("maroon five", "en-US"));
        Assert.Empty(SpokenNumbers.QueryVariants("pink floyd", "fr-FR"));
        Assert.Equal(new[] { "u2" }, SpokenNumbers.QueryVariants("u 2", "fr-FR"));
        Assert.Equal(new[] { "M83" }, SpokenNumbers.QueryVariants("M 83", "fr-FR"));
        Assert.Empty(SpokenNumbers.QueryVariants("m83", "fr-FR"));
    }

    [Fact]
    public void DigitSynonyms_SplitsAndSpellsDigits()
    {
        Assert.Equal(new[] { "U 2", "U deux" }, SpokenNumbers.DigitSynonyms("U2", "fr-FR"));
        Assert.Equal(new[] { "M 83", "M quatre-vingt-trois" }, SpokenNumbers.DigitSynonyms("M83", "fr-FR"));
        Assert.Equal(new[] { "Blink 182", "Blink one hundred eighty-two" }, SpokenNumbers.DigitSynonyms("Blink-182", "en-US"));
        Assert.Equal(new[] { "Maroon five" }, SpokenNumbers.DigitSynonyms("Maroon 5", "en-US"));
        Assert.Equal(new[] { "U 2", "U two" }, SpokenNumbers.DigitSynonyms("U2", "de-DE"));
        Assert.Empty(SpokenNumbers.DigitSynonyms("Pink Floyd", "fr-FR"));
    }

    [Fact]
    public void GenerateSynonyms_AppendsDigitFormsAfterThePhoneticOnes()
    {
        List<string> fr = PhoneticSynonymGenerator.GenerateSynonyms("M83", "fr-FR");
        Assert.Contains("M quatre-vingt-trois", fr);
        Assert.Contains("M 83", fr);

        List<string> en = PhoneticSynonymGenerator.GenerateSynonyms("U2", "en-US");
        Assert.Equal(new[] { "U 2", "U two" }, en);
    }
}
