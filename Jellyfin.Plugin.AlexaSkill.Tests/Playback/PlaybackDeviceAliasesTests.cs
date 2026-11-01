using System;
using System.Collections.Generic;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// Split-endpoint speakers (Sonos Era 100): a voice play arrives from the microphone
/// endpoint id while the AudioPlayer events arrive from a distinct audio endpoint id.
/// These tests pin the token-based re-keying that routes the events back to the queue
/// of the device the stream was issued for, and the cases that must keep their own id.
/// </summary>
public class PlaybackDeviceAliasesTests
{
    private const string VoiceDevice = "amzn1.ask.device.VOICE";
    private const string AudioDevice = "amzn1.ask.device.AUDIO";
    private const string OtherDevice = "amzn1.ask.device.OTHER";

    private DateTimeOffset _now = new(2026, 10, 30, 21, 33, 0, TimeSpan.Zero);

    private PlaybackDeviceAliases NewAliases() => new(() => _now);

    [Fact]
    public void EventFromAudioEndpoint_ReKeyedToVoiceDeviceThatIssuedTheToken()
    {
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        aliases.RecordIssued(VoiceDevice, token);

        Assert.Equal(VoiceDevice, aliases.ResolveOwner(AudioDevice, token));
    }

    [Fact]
    public void RequestFromTheIssuingDevice_KeepsItsOwnId()
    {
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        aliases.RecordIssued(VoiceDevice, token);

        Assert.Null(aliases.ResolveOwner(VoiceDevice, token));
    }

    [Fact]
    public void DeviceThatAlsoIssuedTheToken_KeepsItsOwnId()
    {
        // Two devices playing the same item from their own launches stay independent.
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        aliases.RecordIssued(AudioDevice, token);
        _now = _now.AddMinutes(1);
        aliases.RecordIssued(VoiceDevice, token);

        Assert.Null(aliases.ResolveOwner(AudioDevice, token));
    }

    [Fact]
    public void SeveralOtherIssuers_MostRecentWins()
    {
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        aliases.RecordIssued(OtherDevice, token);
        _now = _now.AddMinutes(1);
        aliases.RecordIssued(VoiceDevice, token);

        Assert.Equal(VoiceDevice, aliases.ResolveOwner(AudioDevice, token));
    }

    [Fact]
    public void UnknownToken_NoReKey()
    {
        var aliases = NewAliases();
        aliases.RecordIssued(VoiceDevice, Guid.NewGuid().ToString());

        Assert.Null(aliases.ResolveOwner(AudioDevice, Guid.NewGuid().ToString()));
    }

    [Theory]
    [InlineData(null, "token")]
    [InlineData("", "token")]
    [InlineData(AudioDevice, null)]
    [InlineData(AudioDevice, "")]
    public void MissingDeviceOrToken_NoReKey(string? deviceId, string? token)
    {
        var aliases = NewAliases();
        aliases.RecordIssued(VoiceDevice, "token");

        Assert.Null(aliases.ResolveOwner(deviceId, token));
    }

    [Fact]
    public void IssueOlderThanRetention_NoReKey()
    {
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        aliases.RecordIssued(VoiceDevice, token);
        _now = _now + PlaybackDeviceAliases.Retention + TimeSpan.FromMinutes(1);

        Assert.Null(aliases.ResolveOwner(AudioDevice, token));
    }

    [Fact]
    public void StaleOwnIssue_DoesNotBlockAFreshOtherIssuer()
    {
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        aliases.RecordIssued(AudioDevice, token);
        _now = _now + PlaybackDeviceAliases.Retention + TimeSpan.FromMinutes(1);
        aliases.RecordIssued(VoiceDevice, token);

        Assert.Equal(VoiceDevice, aliases.ResolveOwner(AudioDevice, token));
    }

    [Fact]
    public void RecordIssuedTokens_RecordsEveryPlayDirective_IgnoresOthers()
    {
        var aliases = NewAliases();
        string first = Guid.NewGuid().ToString();
        string next = Guid.NewGuid().ToString();
        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                Directives = new List<IDirective>
                {
                    PlayDirective(PlayBehavior.ReplaceAll, first),
                    PlayDirective(PlayBehavior.Enqueue, next),
                    new StopDirective(),
                },
            },
        };

        aliases.RecordIssuedTokens(VoiceDevice, response);

        Assert.Equal(VoiceDevice, aliases.ResolveOwner(AudioDevice, first));
        Assert.Equal(VoiceDevice, aliases.ResolveOwner(AudioDevice, next));
    }

    [Fact]
    public void RecordIssuedTokens_NullResponseOrDevice_IsNoOp()
    {
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody { Directives = new List<IDirective> { PlayDirective(PlayBehavior.ReplaceAll, token) } },
        };

        aliases.RecordIssuedTokens(VoiceDevice, null);
        aliases.RecordIssuedTokens(null, response);

        Assert.Null(aliases.ResolveOwner(AudioDevice, token));
    }

    [Fact]
    public void IssuersOf_ListsLiveIssuers_EmptyWhenUnknown()
    {
        var aliases = NewAliases();
        string token = Guid.NewGuid().ToString();
        aliases.RecordIssued(OtherDevice, token);
        _now = _now + PlaybackDeviceAliases.Retention + TimeSpan.FromMinutes(1);
        aliases.RecordIssued(VoiceDevice, token);

        Assert.Equal(new[] { VoiceDevice }, aliases.IssuersOf(token));
        Assert.Empty(aliases.IssuersOf(Guid.NewGuid().ToString()));
        Assert.Empty(aliases.IssuersOf(null));
    }

    [Fact]
    public void GetPlayingToken_PrefersTheEventToken()
    {
        var request = new SkillRequest
        {
            Context = new Context { AudioPlayer = new PlaybackState { Token = "context-token" } },
            Request = new AudioPlayerRequest { Type = "AudioPlayer.PlaybackNearlyFinished", Token = "event-token" },
        };

        Assert.Equal("event-token", PlaybackDeviceAliases.GetPlayingToken(request));
    }

    [Fact]
    public void GetPlayingToken_IntentFallsBackToTheContextPlayer()
    {
        var request = new SkillRequest
        {
            Context = new Context { AudioPlayer = new PlaybackState { Token = "context-token" } },
            Request = new IntentRequest { Type = "IntentRequest", Intent = new Intent { Name = "AMAZON.NextIntent" } },
        };

        Assert.Equal("context-token", PlaybackDeviceAliases.GetPlayingToken(request));
        Assert.Null(PlaybackDeviceAliases.GetPlayingToken(new SkillRequest { Context = new Context() }));
    }

    private static AudioPlayerPlayDirective PlayDirective(PlayBehavior behavior, string token) => new()
    {
        PlayBehavior = behavior,
        AudioItem = new AudioItem { Stream = new AudioItemStream { Url = "https://example.invalid/stream", Token = token } },
    };
}
