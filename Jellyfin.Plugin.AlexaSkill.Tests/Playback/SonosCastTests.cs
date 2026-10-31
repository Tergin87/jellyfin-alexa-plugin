using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using global::Alexa.NET;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// Sonos native-queue cast (proof of concept): routing decision, response reshaping and
/// the UPnP payload builders.
/// </summary>
public class SonosCastTests
{
    private const string Sonos = "amzn1.ask.device.SONOS";

    [Fact]
    public void ShouldCast_RequiresHostAndKnownDevice()
    {
        var config = new PluginConfiguration { SplitEndpointDeviceIds = new Collection<string> { Sonos } };

        Assert.False(SonosCastRouting.ShouldCast(config, Sonos), "no host configured");

        config.SonosCastHost = "192.168.1.50";
        Assert.True(SonosCastRouting.ShouldCast(config, Sonos), "learned split-endpoint device");
        Assert.False(SonosCastRouting.ShouldCast(config, "amzn1.ask.device.ECHO"), "an Echo keeps the AudioPlayer");
        Assert.False(SonosCastRouting.ShouldCast(config, null));
    }

    [Fact]
    public void ShouldCast_AcceptsExplicitDeviceList()
    {
        var config = new PluginConfiguration { SonosCastHost = "sonos.local", SonosCastDeviceIds = " amzn1.ask.device.A , amzn1.ask.device.B" };

        Assert.True(SonosCastRouting.ShouldCast(config, "amzn1.ask.device.B"));
        Assert.False(SonosCastRouting.ShouldCast(config, "amzn1.ask.device.C"));
    }

    [Fact]
    public void FindLaunch_OnlyReplaceAllPlays()
    {
        var enqueue = ResponseBuilder.AudioPlayerPlay(PlayBehavior.Enqueue, "https://jf/next", "next", "prev", 0);
        Assert.Null(SonosCastRouting.FindLaunch(enqueue));

        var launch = ResponseBuilder.AudioPlayerPlay(PlayBehavior.ReplaceAll, "https://jf/first", "first", 0);
        Assert.Equal("first", SonosCastRouting.FindLaunch(launch)!.AudioItem.Stream.Token);

        Assert.Null(SonosCastRouting.FindLaunch(ResponseBuilder.Tell("hi")));
    }

    [Fact]
    public void ToCastConfirmation_DropsDirectivesAndEndsSession()
    {
        var response = ResponseBuilder.AudioPlayerPlay(PlayBehavior.ReplaceAll, "https://jf/first", "first", 0);
        response.Response.ShouldEndSession = null;
        response.SessionAttributes = new Dictionary<string, object> { ["x"] = 1 };

        SonosCastRouting.ToCastConfirmation(response, "Lecture sur le Sonos.");

        Assert.Empty(response.Response.Directives);
        Assert.Equal("Lecture sur le Sonos.", ((PlainTextOutputSpeech)response.Response.OutputSpeech).Text);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Null(response.SessionAttributes);
    }

    [Fact]
    public void PlayOrder_StartsAtLaunchItemAndFollowsQueue()
    {
        var queue = new DeviceQueue { ItemIds = new List<string> { "a", "b", "c", "d" }, CurrentIndex = 1 };

        Assert.Equal(new[] { "b", "c", "d" }, SonosCastRouting.PlayOrder(queue, "B"));
        Assert.Equal(new[] { "z" }, SonosCastRouting.PlayOrder(queue, "z"));
        Assert.Equal(new[] { "z" }, SonosCastRouting.PlayOrder(null, "z"));

        // Queue ids are dashed GUIDs, stream tokens may be "N"-format: same item either way.
        var guidQueue = new DeviceQueue { ItemIds = new List<string> { "106c34c9-6c83-9e9a-25ed-a3e45af1cfca", "7d508028-c98e-0445-0f30-c38f8eea7649" } };
        Assert.Equal(new[] { "7d508028-c98e-0445-0f30-c38f8eea7649" }, SonosCastRouting.PlayOrder(guidQueue, "7d508028c98e04450f30c38f8eea7649"));
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "audio/mp4")]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("flac", "audio/flac")]
    [InlineData("ogg", "audio/ogg")]
    [InlineData(null, "audio/mpeg")]
    [InlineData("weird", "audio/mpeg")]
    public void MimeTypeForContainer_MapsJellyfinContainers(string? container, string expected)
    {
        Assert.Equal(expected, SonosQueueCaster.MimeTypeForContainer(container));
    }

    [Fact]
    public void ParseUdn_ReadsRinconIdFromDeviceDescription()
    {
        const string xml = "<root><device><friendlyName>Salon</friendlyName><UDN>uuid:RINCON_ABC123</UDN></device></root>";

        Assert.Equal("RINCON_ABC123", SonosQueueCaster.ParseUdn(xml));
        Assert.Equal("x-rincon-queue:RINCON_ABC123#0", SonosQueueCaster.QueueUri("RINCON_ABC123"));
        Assert.Null(SonosQueueCaster.ParseUdn("<root/>"));
    }

    [Fact]
    public void BuildDidlMetadata_EscapesAndDescribesTheTrack()
    {
        var track = new SonosTrack("https://jf/Audio/1/stream?static=true&api_key=k", "L'Amour & la machine", "Alain Souchon", "C'est déjà ça", "https://jf/Items/1/Images/Primary", "audio/mp4");

        string didl = SonosQueueCaster.BuildDidlMetadata(track);

        Assert.Contains("<res protocolInfo=\"http-get:*:audio/mp4:*\">https://jf/Audio/1/stream?static=true&amp;api_key=k</res>", didl, StringComparison.Ordinal);
        Assert.Contains("<dc:title>L&apos;Amour &amp; la machine</dc:title>", didl, StringComparison.Ordinal);
        Assert.Contains("<dc:creator>Alain Souchon</dc:creator>", didl, StringComparison.Ordinal);
        Assert.Contains("<upnp:class>object.item.audioItem.musicTrack</upnp:class>", didl, StringComparison.Ordinal);
        Assert.Contains("<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">RINCON_AssociatedZPUDN</desc>", didl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0:01:23", 83L * TimeSpan.TicksPerSecond)]
    [InlineData("1:02:03.500", (3723L * TimeSpan.TicksPerSecond) + (TimeSpan.TicksPerSecond / 2))]
    [InlineData("NOT_IMPLEMENTED", 0L)]
    [InlineData(null, 0L)]
    public void ParseHmsToTicks_ReadsSonosTimes(string? hms, long expected)
    {
        Assert.Equal(expected, SonosQueueCaster.ParseHmsToTicks(hms));
    }

    [Fact]
    public void TryParseJellyfinAudioItem_RecognisesOurStreamUrls()
    {
        Assert.True(SonosQueueCaster.TryParseJellyfinAudioItem("https://jf/Audio/106c34c96c839e9a25eda3e45af1cfca/stream?static=true&api_key=k", out Guid id));
        Assert.Equal(Guid.Parse("106c34c9-6c83-9e9a-25ed-a3e45af1cfca"), id);
        Assert.False(SonosQueueCaster.TryParseJellyfinAudioItem("x-sonos-spotify:spotify%3atrack%3a123", out _));
        Assert.False(SonosQueueCaster.TryParseJellyfinAudioItem(null, out _));
    }

    [Fact]
    public void ParsePlaybackState_ReadsTransportAndPosition()
    {
        const string transport = "<s:Envelope><s:Body><u:GetTransportInfoResponse><CurrentTransportState>PAUSED_PLAYBACK</CurrentTransportState><CurrentTransportStatus>OK</CurrentTransportStatus></u:GetTransportInfoResponse></s:Body></s:Envelope>";
        const string position = "<s:Envelope><s:Body><u:GetPositionInfoResponse><Track>3</Track><TrackDuration>0:04:10</TrackDuration><TrackURI>https://jf/Audio/106c34c96c839e9a25eda3e45af1cfca/stream?static=true&amp;api_key=k</TrackURI><RelTime>0:01:05</RelTime></u:GetPositionInfoResponse></s:Body></s:Envelope>";

        SonosPlaybackState state = SonosQueueCaster.ParsePlaybackState(transport, position);

        Assert.Equal("PAUSED_PLAYBACK", state.TransportState);
        Assert.True(state.IsPaused);
        Assert.True(state.IsActive);
        Assert.Equal(3, state.TrackNumber);
        Assert.Equal("https://jf/Audio/106c34c96c839e9a25eda3e45af1cfca/stream?static=true&api_key=k", state.TrackUri);
        Assert.Equal(65L * TimeSpan.TicksPerSecond, state.PositionTicks);
        Assert.Equal(250L * TimeSpan.TicksPerSecond, state.DurationTicks);
    }

    [Fact]
    public void BuildSoapEnvelope_WrapsActionAndEscapesArguments()
    {
        string soap = SonosQueueCaster.BuildSoapEnvelope(
            "AddURIToQueue",
            new[] { new KeyValuePair<string, string>("InstanceID", "0"), new KeyValuePair<string, string>("EnqueuedURIMetaData", "<DIDL-Lite/>") });

        Assert.StartsWith("<?xml version=\"1.0\"?><s:Envelope", soap, StringComparison.Ordinal);
        Assert.Contains("<u:AddURIToQueue xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">", soap, StringComparison.Ordinal);
        Assert.Contains("<InstanceID>0</InstanceID>", soap, StringComparison.Ordinal);
        Assert.Contains("<EnqueuedURIMetaData>&lt;DIDL-Lite/&gt;</EnqueuedURIMetaData>", soap, StringComparison.Ordinal);
        Assert.EndsWith("</u:AddURIToQueue></s:Body></s:Envelope>", soap, StringComparison.Ordinal);
    }
}
