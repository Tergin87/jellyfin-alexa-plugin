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
    [InlineData("AMAZON.PauseIntent", SonosTransportControl.Operation.Pause)]
    [InlineData("AMAZON.ResumeIntent", SonosTransportControl.Operation.Resume)]
    [InlineData("AMAZON.StopIntent", SonosTransportControl.Operation.Stop)]
    [InlineData("AMAZON.CancelIntent", SonosTransportControl.Operation.Stop)]
    [InlineData("AMAZON.NextIntent", SonosTransportControl.Operation.Next)]
    [InlineData("AMAZON.PreviousIntent", SonosTransportControl.Operation.Previous)]
    [InlineData("AMAZON.StartOverIntent", SonosTransportControl.Operation.StartOver)]
    [InlineData("AMAZON.ShuffleOnIntent", SonosTransportControl.Operation.ShuffleOn)]
    [InlineData("ShuffleAllOffIntent", SonosTransportControl.Operation.ShuffleOff)]
    [InlineData("AMAZON.LoopOnIntent", SonosTransportControl.Operation.RepeatAll)]
    [InlineData("LoopSongOnIntent", SonosTransportControl.Operation.RepeatOne)]
    [InlineData("LoopAllOffIntent", SonosTransportControl.Operation.RepeatOff)]
    [InlineData("MediaInfoIntent", SonosTransportControl.Operation.WhatsPlaying)]
    public void TryMapIntent_MapsTransportIntents(string intent, SonosTransportControl.Operation expected)
    {
        Assert.True(SonosTransportControl.TryMapIntent(intent, out var operation));
        Assert.Equal(expected, operation);
    }

    [Fact]
    public void TryMapIntent_LeavesOtherIntentsToTheHandlers()
    {
        Assert.False(SonosTransportControl.TryMapIntent("PlayAlbumIntent", out _));
        Assert.False(SonosTransportControl.TryMapIntent("AddToQueueIntent", out _));
        Assert.False(SonosTransportControl.TryMapIntent(null, out _));
    }

    [Theory]
    [InlineData("NORMAL", SonosTransportControl.Operation.ShuffleOn, "SHUFFLE_NOREPEAT")]
    [InlineData("REPEAT_ALL", SonosTransportControl.Operation.ShuffleOn, "SHUFFLE")]
    [InlineData("SHUFFLE", SonosTransportControl.Operation.ShuffleOff, "REPEAT_ALL")]
    [InlineData("SHUFFLE_NOREPEAT", SonosTransportControl.Operation.RepeatAll, "SHUFFLE")]
    [InlineData("NORMAL", SonosTransportControl.Operation.RepeatOne, "REPEAT_ONE")]
    [InlineData("SHUFFLE_REPEAT_ONE", SonosTransportControl.Operation.RepeatOff, "SHUFFLE_NOREPEAT")]
    [InlineData(null, SonosTransportControl.Operation.RepeatAll, "REPEAT_ALL")]
    [InlineData("WEIRD", SonosTransportControl.Operation.ShuffleOff, "NORMAL")]
    public void ComposePlayMode_KeepsTheOtherAxis(string? current, SonosTransportControl.Operation operation, string expected)
    {
        Assert.Equal(expected, SonosTransportControl.ComposePlayMode(current, operation));
    }

    [Fact]
    public void RecentAlexaDevices_RecordsLatestStateAndStickyFlags()
    {
        RecentAlexaDevices.Clear();
        var t0 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

        RecentAlexaDevices.Record("amzn1.ask.device.A", "PlayAlbumIntent", hasDisplay: false, splitEndpointLearned: false, t0);
        RecentAlexaDevices.Record("amzn1.ask.device.A", "AudioPlayer.PlaybackStarted", hasDisplay: false, splitEndpointLearned: true, t0.AddSeconds(3));
        RecentAlexaDevices.Record("amzn1.ask.device.B", "LaunchRequest", hasDisplay: true, splitEndpointLearned: false, t0.AddSeconds(1));
        RecentAlexaDevices.Record(null, "ignored", false, false, t0);

        var snapshot = RecentAlexaDevices.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("amzn1.ask.device.A", snapshot[0].DeviceId);
        Assert.Equal("AudioPlayer.PlaybackStarted", snapshot[0].LastRequest);
        Assert.Equal(t0, snapshot[0].FirstSeenUtc);
        Assert.True(snapshot[0].SplitEndpointLearned);
        Assert.False(snapshot[0].HasDisplay);
        Assert.True(snapshot[1].HasDisplay);
        RecentAlexaDevices.Clear();
    }

    [Fact]
    public void IsQueueIntent_RecognisesTheTwoQueueIntents()
    {
        Assert.True(SonosCastRouting.IsQueueIntent("AddToQueueIntent", out bool asNext));
        Assert.False(asNext);
        Assert.True(SonosCastRouting.IsQueueIntent("PlayNextIntent", out asNext));
        Assert.True(asNext);
        Assert.False(SonosCastRouting.IsQueueIntent("PlayAlbumIntent", out _));
        Assert.False(SonosCastRouting.IsQueueIntent(null, out _));
    }

    [Fact]
    public void NewItemIds_DiffsTheQueueAcrossFormatsAndDuplicates()
    {
        var before = new List<string> { "a", "106c34c9-6c83-9e9a-25ed-a3e45af1cfca", "c" };
        var after = new List<string> { "a", "x", "106c34c96c839e9a25eda3e45af1cfca", "c", "a" };

        Assert.Equal(new[] { "x", "a" }, SonosCastRouting.NewItemIds(before, after));
        Assert.Empty(SonosCastRouting.NewItemIds(before, before));
        Assert.Empty(SonosCastRouting.NewItemIds(before, null));
        Assert.Equal(new[] { "z" }, SonosCastRouting.NewItemIds(null, new List<string> { "z" }));
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
        var track = new SonosTrack("https://jf/Audio/1/stream?static=true&api_key=k", "L'Amour & la machine", "Alain Souchon", "C'est déjà ça", "https://jf/Items/1/Images/Primary", "audio/mp4", "106c34c96c839e9a25eda3e45af1cfca");

        string didl = SonosQueueCaster.BuildDidlMetadata(track);

        Assert.Contains("<item id=\"106c34c96c839e9a25eda3e45af1cfca\" parentID=\"-1\" restricted=\"true\">", didl, StringComparison.Ordinal);
        Assert.Contains("<item id=\"-1\"", SonosQueueCaster.BuildDidlMetadata(track with { Id = null }), StringComparison.Ordinal);

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
