#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Links the device that RECEIVED a play command to the device that PLAYS it, when Alexa
/// reports them under two different device ids. Live case (Sonos Era 100, Alexa built-in,
/// 2026-10-30): a voice PlayAlbumIntent spoken to the speaker arrives from its microphone
/// endpoint id, while every AudioPlayer event of the resulting stream arrives from a
/// distinct audio endpoint id. Every per-device store (the device queue, the Jellyfin
/// session queue, the precompute cache) is keyed by the request's device id, so the
/// events found no queue: NearlyFinished enqueued nothing and every album stopped after
/// its first track ("queue exhausted (queueCount=0, finishedIndex=-1)"). A play launched
/// from the Alexa app targets the audio endpoint directly and was never affected.
/// <para>
/// The link is made by stream token: every AudioPlayer.Play directive the skill returns
/// is recorded against the device id it was issued for, and a later request whose
/// AudioPlayer token was issued ONLY for other devices is re-keyed to the most recent
/// issuer. A device that issued the token itself keeps its own id, so an Echo (one id for
/// voice and audio) is never re-keyed, nor is a device playing the same item from its own
/// launch. In-memory only: after a server restart the link is re-made at the next play.
/// </para>
/// </summary>
public sealed class PlaybackDeviceAliases
{
    private const int PruneThreshold = 512;

    /// <summary>How long an issued token can re-key requests (longer than any album or audiobook part).</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    // token -> (issuing device id -> issue time)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _issued = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackDeviceAliases"/> class.
    /// </summary>
    /// <param name="clock">Time source; null uses the UTC wall clock.</param>
    public PlaybackDeviceAliases(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Gets the process-wide instance the skill controller uses.</summary>
    public static PlaybackDeviceAliases Shared { get; } = new();

    /// <summary>
    /// The stream token a request reports as playing: the AudioPlayer event's own token
    /// first (it names the stream the event is about), the context player state otherwise.
    /// </summary>
    /// <param name="request">The deserialized skill request.</param>
    /// <returns>The playing token, or null when the request carries none.</returns>
    public static string? GetPlayingToken(SkillRequest? request)
    {
        string? eventToken = (request?.Request as AudioPlayerRequest)?.Token;
        return string.IsNullOrEmpty(eventToken) ? request?.Context?.AudioPlayer?.Token : eventToken;
    }

    /// <summary>
    /// Records every AudioPlayer.Play token of a response as issued for a device.
    /// </summary>
    /// <param name="deviceId">The device id the response answers.</param>
    /// <param name="response">The skill response about to be returned.</param>
    public void RecordIssuedTokens(string? deviceId, SkillResponse? response)
    {
        if (string.IsNullOrEmpty(deviceId) || response?.Response?.Directives == null)
        {
            return;
        }

        foreach (AudioPlayerPlayDirective play in response.Response.Directives.OfType<AudioPlayerPlayDirective>())
        {
            string? token = play.AudioItem?.Stream?.Token;
            if (!string.IsNullOrEmpty(token))
            {
                RecordIssued(deviceId, token);
            }
        }
    }

    /// <summary>
    /// Records one stream token as issued for a device.
    /// </summary>
    /// <param name="deviceId">The issuing device id.</param>
    /// <param name="token">The stream token.</param>
    public void RecordIssued(string deviceId, string token)
    {
        DateTimeOffset now = _clock();
        _issued.GetOrAdd(token, _ => new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal))[deviceId] = now;

        if (_issued.Count > PruneThreshold)
        {
            Prune(now);
        }
    }

    /// <summary>
    /// Resolves the device a request should be keyed to.
    /// </summary>
    /// <param name="deviceId">The device id the request reports.</param>
    /// <param name="playingToken">The stream token the request reports as playing.</param>
    /// <returns>The most recent other device that issued the token, or null when the request keeps its own id.</returns>
    public string? ResolveOwner(string? deviceId, string? playingToken)
    {
        if (string.IsNullOrEmpty(deviceId)
            || string.IsNullOrEmpty(playingToken)
            || !_issued.TryGetValue(playingToken, out ConcurrentDictionary<string, DateTimeOffset>? issuers))
        {
            return null;
        }

        DateTimeOffset cutoff = _clock() - Retention;
        if (issuers.TryGetValue(deviceId, out DateTimeOffset ownIssue) && ownIssue >= cutoff)
        {
            return null;
        }

        string? owner = null;
        DateTimeOffset latest = cutoff;
        foreach (var (issuer, issuedAt) in issuers)
        {
            if (issuedAt >= latest && !string.Equals(issuer, deviceId, StringComparison.Ordinal))
            {
                owner = issuer;
                latest = issuedAt;
            }
        }

        return owner;
    }

    private void Prune(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - Retention;
        foreach (var (token, issuers) in _issued)
        {
            if (issuers.Values.All(issuedAt => issuedAt < cutoff))
            {
                _issued.TryRemove(token, out _);
            }
        }
    }
}
