#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Wire-shape compatibility for split-endpoint speakers (Sonos Era 100, see
/// <see cref="PlaybackDeviceAliases"/>). On that speaker the plugin's ENQUEUE is accepted
/// without error but never played (the enqueued URL is never even fetched), while a
/// self-hosted Python skill chained tracks on the same speaker. That skill's play
/// responses differed in four ways, all reproduced here for the learned devices only:
/// <list type="bullet">
/// <item>shouldEndSession omitted on every play response (the plugin sends true);</item>
/// <item>stream tokens as 32-digit item ids ("N" format) instead of dashed GUIDs;</item>
/// <item>the Jellyfin <c>/Audio/{id}/universal</c> URL instead of <c>/stream?static=true</c>;</item>
/// <item>metadata art only, no backgroundImage.</item>
/// </list>
/// Tokens are translated at the controller boundary only: inbound "N" tokens are
/// normalized back to the dashed form every handler, store and codec expects.
/// </summary>
public static class SplitEndpointCompatibility
{
    private static readonly object RememberLock = new();

    /// <summary>
    /// Whether a device id was learned as a split-endpoint speaker.
    /// </summary>
    /// <param name="knownDeviceIds">The persisted learned device ids.</param>
    /// <param name="deviceId">The (re-keyed) request device id.</param>
    /// <returns>True for a learned split-endpoint device.</returns>
    public static bool IsKnown(Collection<string> knownDeviceIds, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        lock (RememberLock)
        {
            return knownDeviceIds.Contains(deviceId);
        }
    }

    /// <summary>
    /// Learns a split-endpoint device id.
    /// </summary>
    /// <param name="knownDeviceIds">The persisted learned device ids.</param>
    /// <param name="deviceId">The device id the playing stream was issued for.</param>
    /// <returns>True when the id was new (the caller persists the configuration).</returns>
    public static bool Remember(Collection<string> knownDeviceIds, string deviceId)
    {
        lock (RememberLock)
        {
            if (knownDeviceIds.Contains(deviceId))
            {
                return false;
            }

            knownDeviceIds.Add(deviceId);
            return true;
        }
    }

    /// <summary>
    /// Normalizes a token received from the device to the plugin's internal dashed form.
    /// </summary>
    /// <param name="token">The token as the device reported it.</param>
    /// <returns>The dashed GUID for an "N"-format GUID token; any other token unchanged.</returns>
    public static string? NormalizeInboundToken(string? token)
        => token is { Length: 32 } && Guid.TryParseExact(token, "N", out Guid id) ? id.ToString("D") : token;

    /// <summary>
    /// Converts an internal token to the wire form sent to split-endpoint devices.
    /// </summary>
    /// <param name="token">The internal (dashed) token.</param>
    /// <returns>The "N"-format GUID for a plain dashed GUID; composite tokens unchanged.</returns>
    public static string? ToWireToken(string? token)
        => token != null && Guid.TryParseExact(token, "D", out Guid id) ? id.ToString("N") : token;

    /// <summary>
    /// Normalizes the request's AudioPlayer tokens in place (event token and context player
    /// state). Harmless for every other device: the plugin never issues "N"-format tokens to them.
    /// </summary>
    /// <param name="request">The deserialized skill request.</param>
    public static void NormalizeInboundTokens(SkillRequest request)
    {
        if (request.Request is AudioPlayerRequest audioPlayerRequest)
        {
            audioPlayerRequest.Token = NormalizeInboundToken(audioPlayerRequest.Token);
        }

        if (request.Context?.AudioPlayer != null)
        {
            request.Context.AudioPlayer.Token = NormalizeInboundToken(request.Context.AudioPlayer.Token);
        }
    }

    /// <summary>
    /// Rewrites a plain static audio stream URL to the Jellyfin universal audio URL the
    /// self-hosted skill used. Any other URL (token-gated speed route, video, HLS) is kept.
    /// </summary>
    /// <param name="url">The stream URL built by the launch builder.</param>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <returns>The universal URL, or null when the URL is not a plain static audio stream.</returns>
    public static string? ToUniversalUrl(string? url, Guid jellyfinUserId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.TrimEnd('/').Split('/');
        if (segments.Length < 3
            || !string.Equals(segments[^1], "stream", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[^3], "Audio", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(segments[^2], out Guid itemId))
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string? apiKey = query["api_key"];
        if (query.Count != 2 || !string.Equals(query["static"], "true", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        string basePath = string.Join('/', segments[..^3]);
        string item = itemId.ToString("N");
        string user = jellyfinUserId.ToString("N");
        return $"{uri.Scheme}://{uri.Authority}{basePath}/Audio/{item}/universal"
            + $"?UserId={user}&DeviceId=NONE&MaxStreamingBitrate=140000000"
            + $"&ApiKey={Uri.EscapeDataString(apiKey)}&AudioCodec=mp3";
    }

    /// <summary>
    /// Gives a response's AudioPlayer.Play directives the self-hosted skill's wire shape.
    /// </summary>
    /// <param name="response">The skill response about to be returned.</param>
    /// <param name="jellyfinUserId">The Jellyfin user id for the universal URL.</param>
    /// <param name="rawPlayingToken">The playing token exactly as the device sent it (before normalization).</param>
    /// <returns>True when the response carried a Play directive and was rewritten.</returns>
    public static bool Apply(SkillResponse response, Guid jellyfinUserId, string? rawPlayingToken)
    {
        AudioPlayerPlayDirective[] plays = response.Response?.Directives?.OfType<AudioPlayerPlayDirective>().ToArray() ?? Array.Empty<AudioPlayerPlayDirective>();
        if (plays.Length == 0)
        {
            return false;
        }

        foreach (AudioPlayerPlayDirective play in plays)
        {
            AudioItemStream? stream = play.AudioItem?.Stream;
            if (stream != null)
            {
                stream.Url = ToUniversalUrl(stream.Url, jellyfinUserId) ?? stream.Url;
                stream.Token = ToWireToken(stream.Token);

                // The expected token must be the one the device itself plays: a stream issued
                // before the device was learned still carries its dashed token.
                if (stream.ExpectedPreviousToken != null)
                {
                    stream.ExpectedPreviousToken = rawPlayingToken != null && NormalizeInboundToken(rawPlayingToken) == stream.ExpectedPreviousToken
                        ? rawPlayingToken
                        : ToWireToken(stream.ExpectedPreviousToken);
                }
            }

            if (play.AudioItem?.Metadata != null)
            {
                play.AudioItem.Metadata.BackgroundImage = null;
            }
        }

        response.Response!.ShouldEndSession = null;
        return true;
    }
}
