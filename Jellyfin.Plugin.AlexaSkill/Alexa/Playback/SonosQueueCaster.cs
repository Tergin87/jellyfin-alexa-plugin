using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Pushes tracks into a Sonos speaker's native queue over its UPnP AVTransport service
/// (port 1400, the protocol the Sonos app and SoCo use) and starts playback from the first
/// one. Proof of concept for the Sonos Era 100, where Alexa's AudioPlayer ENQUEUE is
/// accepted but never played: the speaker's own queue chains tracks natively and Alexa on
/// the speaker already controls it by voice, so the skill only has to fill it.
/// </summary>
/// <remarks>
/// Sequence (SoCo's <c>add_uri_to_queue</c> + <c>play_from_queue</c>): read the player's
/// UDN once, <c>RemoveAllTracksFromQueue</c>, <c>AddURIToQueue</c> for the first track,
/// <c>SetAVTransportURI</c> to the player's own queue (<c>x-rincon-queue:RINCON#0</c>),
/// <c>Seek TRACK_NR 1</c>, <c>Play</c>. The remaining tracks are appended in the
/// background so the Alexa response stays well inside its budget.
/// </remarks>
public sealed class SonosQueueCaster
{
    private const string AvTransportService = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string AvTransportControlPath = "/MediaRenderer/AVTransport/Control";
    private const string DeviceDescriptionPath = "/xml/device_description.xml";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TailTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex UdnPattern = new("<UDN>\\s*uuid:([^<\\s]+)\\s*</UDN>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SonosQueueCaster> _logger;
    private readonly ConcurrentDictionary<string, string> _udnByHost = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="SonosQueueCaster"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SonosQueueCaster(IHttpClientFactory httpClientFactory, ILogger<SonosQueueCaster> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Replaces the speaker's queue with the given tracks and starts playing the first one.
    /// </summary>
    /// <param name="host">The speaker's LAN host (IP address or name).</param>
    /// <param name="tracks">The tracks, in play order (at least one).</param>
    /// <param name="cancellationToken">The request cancellation token (the Alexa budget).</param>
    /// <param name="moreTracks">Optional source of further tracks (the progressive queue's remaining pages), called repeatedly in the background until it returns an empty list.</param>
    /// <returns>True when the first track was queued and playback started; false on any UPnP or network failure (logged).</returns>
    public async Task<bool> CastAsync(string host, IReadOnlyList<SonosTrack> tracks, CancellationToken cancellationToken, Func<CancellationToken, Task<IReadOnlyList<SonosTrack>>>? moreTracks = null)
    {
        if (string.IsNullOrWhiteSpace(host) || tracks == null || tracks.Count == 0)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string udn = await GetUdnAsync(host, cancellationToken).ConfigureAwait(false);

            await InvokeAsync(host, "RemoveAllTracksFromQueue", new[] { Arg("InstanceID", "0") }, cancellationToken).ConfigureAwait(false);
            await AddToQueueAsync(host, tracks[0], cancellationToken).ConfigureAwait(false);
            await InvokeAsync(
                host,
                "SetAVTransportURI",
                new[] { Arg("InstanceID", "0"), Arg("CurrentURI", QueueUri(udn)), Arg("CurrentURIMetaData", string.Empty) },
                cancellationToken).ConfigureAwait(false);
            await InvokeAsync(host, "Seek", new[] { Arg("InstanceID", "0"), Arg("Unit", "TRACK_NR"), Arg("Target", "1") }, cancellationToken).ConfigureAwait(false);
            await InvokeAsync(host, "Play", new[] { Arg("InstanceID", "0"), Arg("Speed", "1") }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Sonos cast: playback of \"{Title}\" started on {Host} in {ElapsedMs} ms; {Remaining} more track(s) queued in the background",
                tracks[0].Title, host, stopwatch.ElapsedMilliseconds, tracks.Count - 1);

            if (tracks.Count > 1 || moreTracks != null)
            {
                _ = Task.Run(() => AppendTailAsync(host, tracks, moreTracks), CancellationToken.None);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Sonos cast: could not start playback on {Host} after {ElapsedMs} ms", host, stopwatch.ElapsedMilliseconds);
            return false;
        }
    }

    /// <summary>
    /// Builds the MIME type a Jellyfin container string maps to, for the DIDL resource the
    /// speaker reads before fetching the stream.
    /// </summary>
    /// <param name="container">Jellyfin's item container (possibly a comma list such as "mov,mp4,m4a,3gp,3g2,mj2").</param>
    /// <returns>The MIME type; audio/mpeg when the container is unknown.</returns>
    public static string MimeTypeForContainer(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return "audio/mpeg";
        }

        string[] parts = container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "mp3": return "audio/mpeg";
                case "m4a":
                case "m4b":
                case "mp4":
                case "aac": return "audio/mp4";
                case "flac": return "audio/flac";
                case "ogg":
                case "oga":
                case "opus": return "audio/ogg";
                case "wav": return "audio/wav";
                case "aiff":
                case "aif": return "audio/aiff";
                case "wma": return "audio/x-ms-wma";
            }
        }

        return "audio/mpeg";
    }

    /// <summary>
    /// Builds the queue URI of a player from its UDN (the <c>RINCON_...</c> id).
    /// </summary>
    /// <param name="udn">The player's UDN without the <c>uuid:</c> prefix.</param>
    /// <returns>The <c>x-rincon-queue</c> URI.</returns>
    internal static string QueueUri(string udn) => $"x-rincon-queue:{udn}#0";

    /// <summary>
    /// Extracts the UDN from a Sonos device description document.
    /// </summary>
    /// <param name="deviceDescriptionXml">The <c>/xml/device_description.xml</c> body.</param>
    /// <returns>The UDN without the <c>uuid:</c> prefix, or null when absent.</returns>
    internal static string? ParseUdn(string deviceDescriptionXml)
    {
        Match match = UdnPattern.Match(deviceDescriptionXml ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Builds the DIDL-Lite metadata of a track (title, artist, album, art, resource).
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>The DIDL-Lite document (unescaped; the SOAP builder escapes it as an argument).</returns>
    internal static string BuildDidlMetadata(SonosTrack track)
    {
        var sb = new StringBuilder();
        sb.Append("<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">");
        // A unique id per row: with every item named "-1" the Sonos app's queue view
        // rendered anonymous lines although the stored metadata was complete.
        sb.Append("<item id=\"").Append(SecurityElement.Escape(string.IsNullOrEmpty(track.Id) ? "-1" : track.Id)).Append("\" parentID=\"-1\" restricted=\"true\">");
        sb.Append("<res protocolInfo=\"http-get:*:").Append(SecurityElement.Escape(track.MimeType)).Append(":*\">").Append(SecurityElement.Escape(track.Url)).Append("</res>");
        sb.Append("<dc:title>").Append(SecurityElement.Escape(track.Title)).Append("</dc:title>");
        if (!string.IsNullOrEmpty(track.Artist))
        {
            sb.Append("<dc:creator>").Append(SecurityElement.Escape(track.Artist)).Append("</dc:creator>");
        }

        if (!string.IsNullOrEmpty(track.Album))
        {
            sb.Append("<upnp:album>").Append(SecurityElement.Escape(track.Album)).Append("</upnp:album>");
        }

        if (!string.IsNullOrEmpty(track.ArtUrl))
        {
            sb.Append("<upnp:albumArtURI>").Append(SecurityElement.Escape(track.ArtUrl)).Append("</upnp:albumArtURI>");
        }

        sb.Append("<upnp:class>object.item.audioItem.musicTrack</upnp:class>");
        // Sonos reads the title/artist/art of a plain http item only when the item names
        // its owning service; RINCON_AssociatedZPUDN is the local-player marker every
        // controller library (SoCo, node-sonos) sends for such items.
        sb.Append("<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">RINCON_AssociatedZPUDN</desc>");
        sb.Append("</item></DIDL-Lite>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the SOAP envelope of an AVTransport action.
    /// </summary>
    /// <param name="action">The action name (e.g. <c>Play</c>).</param>
    /// <param name="arguments">The ordered arguments; values are XML-escaped here.</param>
    /// <returns>The SOAP request body.</returns>
    internal static string BuildSoapEnvelope(string action, IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(AvTransportService).Append("\">");
        foreach (KeyValuePair<string, string> argument in arguments)
        {
            sb.Append('<').Append(argument.Key).Append('>').Append(SecurityElement.Escape(argument.Value)).Append("</").Append(argument.Key).Append('>');
        }

        sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return sb.ToString();
    }

    private static KeyValuePair<string, string> Arg(string name, string value) => new(name, value);

    private async Task AppendTailAsync(string host, IReadOnlyList<SonosTrack> tracks, Func<CancellationToken, Task<IReadOnlyList<SonosTrack>>>? moreTracks)
    {
        using var cts = new CancellationTokenSource(TailTimeout);
        int added = 0;
        try
        {
            for (int i = 1; i < tracks.Count; i++)
            {
                await AddToQueueAsync(host, tracks[i], cts.Token).ConfigureAwait(false);
                added++;
            }

            // The progressive queue only materializes its first page at launch (the rest
            // used to arrive on PlaybackNearlyFinished, which a cast never receives):
            // drain the remaining pages here so the speaker holds the whole album.
            if (moreTracks != null)
            {
                while (true)
                {
                    IReadOnlyList<SonosTrack> batch = await moreTracks(cts.Token).ConfigureAwait(false);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    foreach (SonosTrack track in batch)
                    {
                        await AddToQueueAsync(host, track, cts.Token).ConfigureAwait(false);
                        added++;
                    }
                }
            }

            _logger.LogInformation("Sonos cast: {Added} more track(s) appended to the queue on {Host}", added, host);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Sonos cast: queue tail interrupted after {Added} appended track(s) on {Host}", added, host);
        }
    }

    private async Task AddToQueueAsync(string host, SonosTrack track, CancellationToken cancellationToken, bool asNext = false, int position = 0)
    {
        await InvokeAsync(
            host,
            "AddURIToQueue",
            new[]
            {
                Arg("InstanceID", "0"),
                Arg("EnqueuedURI", track.Url),
                Arg("EnqueuedURIMetaData", BuildDidlMetadata(track)),
                // 0 = append; otherwise the 1-based queue position the track takes.
                Arg("DesiredFirstTrackNumberEnqueued", position.ToString(CultureInfo.InvariantCulture)),
                Arg("EnqueueAsNext", asNext ? "1" : "0"),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetUdnAsync(string host, CancellationToken cancellationToken)
    {
        if (_udnByHost.TryGetValue(host, out string? cached))
        {
            return cached;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CallTimeout);
        var client = _httpClientFactory.CreateClient("AlexaSkill");
        string xml = await client.GetStringAsync(new Uri($"http://{host}:1400{DeviceDescriptionPath}"), cts.Token).ConfigureAwait(false);
        string? udn = ParseUdn(xml);
        if (string.IsNullOrEmpty(udn))
        {
            throw new InvalidOperationException($"No UDN in the device description of {host}");
        }

        _udnByHost[host] = udn;
        _logger.LogDebug("Sonos cast: player {Host} has UDN {Udn}", host, udn);
        return udn;
    }

    /// <summary>
    /// Appends tracks to the speaker's current queue without touching playback: at the
    /// end, or right after the playing track ("play next").
    /// </summary>
    /// <param name="host">The speaker's LAN host.</param>
    /// <param name="tracks">The tracks to add, in order.</param>
    /// <param name="asNext">True to insert after the current track instead of appending.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>True when every track was accepted; false on any UPnP or network failure (logged).</returns>
    public async Task<bool> EnqueueAsync(string host, IReadOnlyList<SonosTrack> tracks, bool asNext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || tracks == null || tracks.Count == 0)
        {
            return false;
        }

        try
        {
            // EnqueueAsNext alone only matters in shuffle mode (device-verified: both adds
            // landed at the end of a 14-track queue while track 3 played). In normal order
            // the insertion point must be explicit: the 1-based number of the current track
            // plus one, then one more per extra track so several land in order.
            int position = 0;
            if (asNext)
            {
                SonosPlaybackState? state = await GetPlaybackStateAsync(host, cancellationToken).ConfigureAwait(false);
                position = state is { TrackNumber: > 0 } ? state.TrackNumber + 1 : 0;
            }

            foreach (SonosTrack track in tracks)
            {
                await AddToQueueAsync(host, track, cancellationToken, asNext, position).ConfigureAwait(false);
                if (position > 0)
                {
                    position++;
                }
            }

            _logger.LogInformation(
                "Sonos cast: {Count} track(s) {Where} the queue on {Host}",
                tracks.Count, asNext ? (position > 0 ? $"inserted at position {position - tracks.Count} of" : "appended (no current track) to") : "appended to", host);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Sonos cast: could not add to the queue on {Host}", host);
            return false;
        }
    }

    /// <summary>
    /// Reads what the speaker is doing right now: transport state, current track URI and
    /// position (AVTransport <c>GetTransportInfo</c> + <c>GetPositionInfo</c>).
    /// </summary>
    /// <param name="host">The speaker's LAN host.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state, or null when the speaker did not answer (logged at debug).</returns>
    public async Task<SonosPlaybackState?> GetPlaybackStateAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            string transport = await InvokeAsync(host, "GetTransportInfo", new[] { Arg("InstanceID", "0") }, cancellationToken).ConfigureAwait(false);
            string position = await InvokeAsync(host, "GetPositionInfo", new[] { Arg("InstanceID", "0") }, cancellationToken).ConfigureAwait(false);
            return ParsePlaybackState(transport, position);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Sonos cast: could not read the playback state of {Host}", host);
            return null;
        }
    }

    /// <summary>
    /// Builds the playback state from the two AVTransport answers.
    /// </summary>
    /// <param name="transportInfoBody">The <c>GetTransportInfo</c> SOAP response body.</param>
    /// <param name="positionInfoBody">The <c>GetPositionInfo</c> SOAP response body.</param>
    /// <returns>The parsed state.</returns>
    internal static SonosPlaybackState ParsePlaybackState(string transportInfoBody, string positionInfoBody)
    {
        string state = ParseSoapValue(transportInfoBody, "CurrentTransportState") ?? "UNKNOWN";
        string? trackUri = ParseSoapValue(positionInfoBody, "TrackURI");
        int.TryParse(ParseSoapValue(positionInfoBody, "Track"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int track);
        return new SonosPlaybackState(
            state,
            string.IsNullOrWhiteSpace(trackUri) ? null : System.Net.WebUtility.HtmlDecode(trackUri),
            track,
            ParseHmsToTicks(ParseSoapValue(positionInfoBody, "RelTime")),
            ParseHmsToTicks(ParseSoapValue(positionInfoBody, "TrackDuration")));
    }

    /// <summary>
    /// Reads the text of one element of a SOAP response (the first occurrence).
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <param name="element">The element name.</param>
    /// <returns>The raw (still XML-escaped) text, or null when absent.</returns>
    internal static string? ParseSoapValue(string body, string element)
    {
        Match match = Regex.Match(body ?? string.Empty, $"<{element}>(.*?)</{element}>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Converts a Sonos <c>H:MM:SS</c> time (optionally with fractional seconds) to ticks;
    /// <c>NOT_IMPLEMENTED</c> and malformed values give 0.
    /// </summary>
    /// <param name="hms">The time text.</param>
    /// <returns>The ticks.</returns>
    internal static long ParseHmsToTicks(string? hms)
    {
        if (string.IsNullOrWhiteSpace(hms))
        {
            return 0;
        }

        string[] parts = hms.Trim().Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return 0;
        }

        return (long)Math.Round(((hours * 3600d) + (minutes * 60d) + seconds) * TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Extracts the Jellyfin item id from one of the stream URLs this caster hands the
    /// speaker (<c>/Audio/{id}/stream…</c>).
    /// </summary>
    /// <param name="trackUri">The speaker's current track URI.</param>
    /// <param name="itemId">The item id when the URI is a Jellyfin audio stream.</param>
    /// <returns>True when the URI is a Jellyfin audio stream.</returns>
    public static bool TryParseJellyfinAudioItem(string? trackUri, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(trackUri))
        {
            return false;
        }

        Match match = Regex.Match(trackUri, "/Audio/([0-9a-fA-F-]{32,36})/", RegexOptions.CultureInvariant);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out itemId);
    }

    private async Task<string> InvokeAsync(string host, string action, IReadOnlyList<KeyValuePair<string, string>> arguments, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://{host}:1400{AvTransportControlPath}"));
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{AvTransportService}#{action}\"");
        request.Content = new StringContent(BuildSoapEnvelope(action, arguments), Encoding.UTF8, "text/xml");

        var client = _httpClientFactory.CreateClient("AlexaSkill");
        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // UPnP faults come back as HTTP 500 with an <errorCode> (e.g. 714 illegal seek target, 800 queue full).
            Match code = Regex.Match(body, "<errorCode>(\\d+)</errorCode>", RegexOptions.CultureInvariant);
            string detail = code.Success ? $"UPnP error {code.Groups[1].Value}" : $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"Sonos {action} on {host} failed: {detail}");
        }

        _logger.LogDebug("Sonos cast: {Action} on {Host} answered in {ElapsedMs} ms", action, host, stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
        return body;
    }
}
