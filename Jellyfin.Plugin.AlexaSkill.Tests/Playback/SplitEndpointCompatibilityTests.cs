using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// Sonos Era 100: play responses for learned split-endpoint devices take the wire shape of
/// the self-hosted skill that chained tracks on that speaker; tokens are translated back
/// at the boundary so every handler keeps seeing dashed GUIDs.
/// </summary>
public class SplitEndpointCompatibilityTests
{
    private static readonly Guid UserId = Guid.Parse("5005da0c-b61e-4a0e-a415-bca5821cbc25");
    private static readonly Guid ItemId = Guid.Parse("106c34c9-6c83-9e9a-25ed-a3e45af1cfca");
    private static readonly Guid NextId = Guid.Parse("7d508028-c98e-0445-0f30-c38f8eea7649");

    [Fact]
    public void Tokens_RoundTripBetweenDashedAndNFormat()
    {
        string wire = SplitEndpointCompatibility.ToWireToken(ItemId.ToString())!;

        Assert.Equal("106c34c96c839e9a25eda3e45af1cfca", wire);
        Assert.Equal(ItemId.ToString(), SplitEndpointCompatibility.NormalizeInboundToken(wire));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("106c34c9-6c83-9e9a-25ed-a3e45af1cfca|sleep:638900000000000000")]
    public void Tokens_NonPlainGuidsAreUnchanged(string? token)
    {
        Assert.Equal(token, SplitEndpointCompatibility.ToWireToken(token));
        Assert.Equal(token, SplitEndpointCompatibility.NormalizeInboundToken(token));
    }

    [Fact]
    public void NormalizeInboundTokens_RewritesEventAndContextTokens()
    {
        string wire = ItemId.ToString("N");
        var request = new SkillRequest
        {
            Context = new Context { AudioPlayer = new PlaybackState { Token = wire } },
            Request = new AudioPlayerRequest { Type = "AudioPlayer.PlaybackNearlyFinished", Token = wire },
        };

        SplitEndpointCompatibility.NormalizeInboundTokens(request);

        Assert.Equal(ItemId.ToString(), ((AudioPlayerRequest)request.Request).Token);
        Assert.Equal(ItemId.ToString(), request.Context.AudioPlayer.Token);
    }

    [Fact]
    public void ToUniversalUrl_RewritesPlainStaticAudioStream()
    {
        string url = $"https://jellyfin.example.org/Audio/{ItemId}/stream?static=true&api_key=abc123";

        Assert.Equal(
            $"https://jellyfin.example.org/Audio/{ItemId:N}/universal?UserId={UserId:N}&DeviceId=NONE&MaxStreamingBitrate=140000000&ApiKey=abc123&AudioCodec=mp3",
            SplitEndpointCompatibility.ToUniversalUrl(url, UserId));
    }

    [Fact]
    public void ToUniversalUrl_KeepsTheServerBasePath()
    {
        string url = $"https://example.org/jellyfin/Audio/{ItemId}/stream?static=true&api_key=abc123";

        Assert.StartsWith($"https://example.org/jellyfin/Audio/{ItemId:N}/universal?", SplitEndpointCompatibility.ToUniversalUrl(url, UserId));
    }

    [Theory]
    [InlineData("https://example.org/Videos/106c34c9-6c83-9e9a-25ed-a3e45af1cfca/stream?static=true&api_key=k")]
    [InlineData("https://example.org/Audio/106c34c9-6c83-9e9a-25ed-a3e45af1cfca/stream?static=true&api_key=k&token=signed")]
    [InlineData("https://example.org/alexaskill/audio/106c34c9-6c83-9e9a-25ed-a3e45af1cfca/speed?rate=1250")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void ToUniversalUrl_LeavesOtherUrlsAlone(string? url)
    {
        Assert.Null(SplitEndpointCompatibility.ToUniversalUrl(url, UserId));
    }

    [Fact]
    public void Apply_GivesTheEnqueueTheSelfHostedSkillShape()
    {
        var response = EnqueueResponse(expectedPrevious: ItemId.ToString());

        bool applied = SplitEndpointCompatibility.Apply(response, UserId, rawPlayingToken: ItemId.ToString("N"));

        Assert.True(applied);
        Assert.Null(response.Response.ShouldEndSession);
        var play = Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal(NextId.ToString("N"), play.AudioItem.Stream.Token);
        Assert.Equal(ItemId.ToString("N"), play.AudioItem.Stream.ExpectedPreviousToken);
        Assert.Contains($"/Audio/{NextId:N}/universal?", play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Null(play.AudioItem.Metadata.BackgroundImage);
        Assert.NotNull(play.AudioItem.Metadata.Art);
    }

    [Fact]
    public void Apply_ExpectedPreviousTokenEchoesADashedTokenTheDeviceStillPlays()
    {
        // A stream issued before the device was learned still carries its dashed token.
        var response = EnqueueResponse(expectedPrevious: ItemId.ToString());

        SplitEndpointCompatibility.Apply(response, UserId, rawPlayingToken: ItemId.ToString());

        var play = Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal(ItemId.ToString(), play.AudioItem.Stream.ExpectedPreviousToken);
    }

    [Fact]
    public void Apply_ResponseWithoutPlay_IsUntouched()
    {
        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody { ShouldEndSession = true, Directives = new List<IDirective> { new StopDirective() } },
        };

        Assert.False(SplitEndpointCompatibility.Apply(response, UserId, rawPlayingToken: null));
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void RememberAndIsKnown()
    {
        var known = new Collection<string>();

        Assert.False(SplitEndpointCompatibility.IsKnown(known, "amzn1.ask.device.VOICE"));
        Assert.True(SplitEndpointCompatibility.Remember(known, "amzn1.ask.device.VOICE"));
        Assert.False(SplitEndpointCompatibility.Remember(known, "amzn1.ask.device.VOICE"));
        Assert.True(SplitEndpointCompatibility.IsKnown(known, "amzn1.ask.device.VOICE"));
        Assert.False(SplitEndpointCompatibility.IsKnown(known, null));
    }

    private static SkillResponse EnqueueResponse(string expectedPrevious)
    {
        var image = new AudioItemSources { Sources = new List<AudioItemSource> { new() { Url = "https://example.org/art.jpg" } } };
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                Directives = new List<IDirective>
                {
                    new AudioPlayerPlayDirective
                    {
                        PlayBehavior = PlayBehavior.Enqueue,
                        AudioItem = new AudioItem
                        {
                            Stream = new AudioItemStream
                            {
                                Url = $"https://jellyfin.example.org/Audio/{NextId}/stream?static=true&api_key=abc123",
                                Token = NextId.ToString(),
                                ExpectedPreviousToken = expectedPrevious,
                            },
                            Metadata = new AudioItemMetadata { Title = "La Tchave", Art = image, BackgroundImage = image },
                        },
                    },
                },
            },
        };
    }
}
