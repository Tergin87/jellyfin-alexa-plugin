using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// The transport and information intents addressed to the skill while a Sonos speaker
/// plays our music ("ask Jellyfin to pause", "... what's playing"): Alexa's own context
/// reports nothing playing, so the regular handlers would answer "nothing is playing"
/// or emit AudioPlayer directives the speaker ignores. Here the speaker itself is driven
/// and asked over UPnP, and the skill answers with the strings the handlers use.
/// </summary>
public sealed class SonosTransportControl
{
    private readonly SonosQueueCaster _caster;
    private readonly ILogger<SonosTransportControl> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SonosTransportControl"/> class.
    /// </summary>
    /// <param name="caster">The UPnP client.</param>
    /// <param name="logger">The logger.</param>
    public SonosTransportControl(SonosQueueCaster caster, ILogger<SonosTransportControl> logger)
    {
        _caster = caster;
        _logger = logger;
    }

    /// <summary>
    /// The speaker operations an intent maps to.
    /// </summary>
    public enum Operation
    {
        /// <summary>Pause playback (position kept).</summary>
        Pause,

        /// <summary>Resume playback.</summary>
        Resume,

        /// <summary>Stop playback.</summary>
        Stop,

        /// <summary>Skip to the next queued track.</summary>
        Next,

        /// <summary>Go back to the previous track.</summary>
        Previous,

        /// <summary>Restart the current track.</summary>
        StartOver,

        /// <summary>Shuffle the queue.</summary>
        ShuffleOn,

        /// <summary>Play the queue in order.</summary>
        ShuffleOff,

        /// <summary>Repeat the whole queue.</summary>
        RepeatAll,

        /// <summary>Repeat the current track.</summary>
        RepeatOne,

        /// <summary>Stop repeating.</summary>
        RepeatOff,

        /// <summary>Say what is playing.</summary>
        WhatsPlaying,
    }

    /// <summary>
    /// Maps an intent to a speaker operation.
    /// </summary>
    /// <param name="intentName">The intent name.</param>
    /// <param name="operation">The operation when the intent is one of ours.</param>
    /// <returns>True when the intent is handled on the speaker.</returns>
    public static bool TryMapIntent(string? intentName, out Operation operation)
    {
        switch (intentName)
        {
            case IntentNames.AmazonPause: operation = Operation.Pause; return true;
            case IntentNames.AmazonResume: operation = Operation.Resume; return true;
            case IntentNames.AmazonStop:
            case IntentNames.AmazonCancel: operation = Operation.Stop; return true;
            case IntentNames.AmazonNext: operation = Operation.Next; return true;
            case IntentNames.AmazonPrevious: operation = Operation.Previous; return true;
            case IntentNames.AmazonStartOver:
            case IntentNames.AmazonRepeat: operation = Operation.StartOver; return true;
            case IntentNames.AmazonShuffleOn:
            case IntentNames.ShuffleAllOn: operation = Operation.ShuffleOn; return true;
            case IntentNames.AmazonShuffleOff:
            case IntentNames.ShuffleAllOff: operation = Operation.ShuffleOff; return true;
            case IntentNames.AmazonLoopOn:
            case IntentNames.LoopAllOn: operation = Operation.RepeatAll; return true;
            case IntentNames.LoopSongOn: operation = Operation.RepeatOne; return true;
            case IntentNames.AmazonLoopOff:
            case IntentNames.LoopAllOff: operation = Operation.RepeatOff; return true;
            case IntentNames.MediaInfo: operation = Operation.WhatsPlaying; return true;
            default: operation = default; return false;
        }
    }

    /// <summary>
    /// Composes the Sonos play mode that applies a shuffle/repeat change on top of the
    /// current one (the two axes are independent on the speaker but encoded as one value).
    /// </summary>
    /// <param name="currentMode">The current play mode (null or unknown reads as NORMAL).</param>
    /// <param name="operation">One of the shuffle/repeat operations.</param>
    /// <returns>The new play mode.</returns>
    public static string ComposePlayMode(string? currentMode, Operation operation)
    {
        bool shuffle = currentMode is "SHUFFLE_NOREPEAT" or "SHUFFLE" or "SHUFFLE_REPEAT_ONE";
        string repeat = currentMode switch
        {
            "REPEAT_ALL" or "SHUFFLE" => "all",
            "REPEAT_ONE" or "SHUFFLE_REPEAT_ONE" => "one",
            _ => "off",
        };

        switch (operation)
        {
            case Operation.ShuffleOn: shuffle = true; break;
            case Operation.ShuffleOff: shuffle = false; break;
            case Operation.RepeatAll: repeat = "all"; break;
            case Operation.RepeatOne: repeat = "one"; break;
            case Operation.RepeatOff: repeat = "off"; break;
        }

        return (shuffle, repeat) switch
        {
            (false, "off") => "NORMAL",
            (false, "all") => "REPEAT_ALL",
            (false, _) => "REPEAT_ONE",
            (true, "off") => "SHUFFLE_NOREPEAT",
            (true, "all") => "SHUFFLE",
            (true, _) => "SHUFFLE_REPEAT_ONE",
        };
    }

    /// <summary>
    /// Performs the operation on the speaker and builds the spoken answer.
    /// </summary>
    /// <param name="host">The speaker's LAN host.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="describe">Resolves a Jellyfin item id to (title, artist, album) for the spoken lines; null when unknown.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The response, or null when the speaker refused (the caller falls back to the regular handler).</returns>
    public async Task<SkillResponse?> HandleAsync(
        string host,
        Operation operation,
        string locale,
        Func<Guid, (string Title, string? Artist, string? Album)?> describe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(describe);
        _logger.LogInformation("Sonos transport: {Operation} on {Host}", operation, host);
        switch (operation)
        {
            case Operation.Pause:
                return await _caster.TransportAsync(host, "Pause", cancellationToken).ConfigureAwait(false)
                    ? ResponseBuilder.Tell(ResponseStrings.Get("PauseSessionSpeech", locale))
                    : null;

            case Operation.Resume:
                return await _caster.TransportAsync(host, "Play", cancellationToken, new KeyValuePair<string, string>("Speed", "1")).ConfigureAwait(false)
                    ? ResponseBuilder.Tell(ResponseStrings.Get("ResumeBrief", locale))
                    : null;

            case Operation.Stop:
                return await _caster.TransportAsync(host, "Stop", cancellationToken).ConfigureAwait(false)
                    ? ResponseBuilder.Tell(ResponseStrings.Get("MusicStoppedByVoice", locale))
                    : null;

            case Operation.Next:
            case Operation.Previous:
                // Silent, like the AudioPlayer handlers: the track change is the answer.
                return await _caster.TransportAsync(host, operation == Operation.Next ? "Next" : "Previous", cancellationToken).ConfigureAwait(false)
                    ? ResponseBuilder.Empty()
                    : null;

            case Operation.StartOver:
            {
                SonosPlaybackState? state = await _caster.GetPlaybackStateAsync(host, cancellationToken).ConfigureAwait(false);
                if (!await _caster.TransportAsync(host, "Seek", cancellationToken, new KeyValuePair<string, string>("Unit", "REL_TIME"), new KeyValuePair<string, string>("Target", "0:00:00")).ConfigureAwait(false))
                {
                    return null;
                }

                string? title = Describe(state, describe)?.Title;
                return title != null
                    ? ResponseBuilder.Tell(ResponseStrings.Get("RestartingContent", locale, title))
                    : ResponseBuilder.Empty();
            }

            case Operation.ShuffleOn:
            case Operation.ShuffleOff:
            case Operation.RepeatAll:
            case Operation.RepeatOne:
            case Operation.RepeatOff:
            {
                string? current = await _caster.GetPlayModeAsync(host, cancellationToken).ConfigureAwait(false);
                string mode = ComposePlayMode(current, operation);
                if (!await _caster.TransportAsync(host, "SetPlayMode", cancellationToken, new KeyValuePair<string, string>("NewPlayMode", mode)).ConfigureAwait(false))
                {
                    return null;
                }

                string key = operation switch
                {
                    Operation.ShuffleOn => "ShuffleEnabled",
                    Operation.ShuffleOff => "ShuffleDisabled",
                    Operation.RepeatAll => "RepeatAllEnabled",
                    Operation.RepeatOne => "RepeatSongEnabled",
                    _ => "RepeatDisabled",
                };
                return ResponseBuilder.Tell(ResponseStrings.Get(key, locale));
            }

            case Operation.WhatsPlaying:
            {
                SonosPlaybackState? state = await _caster.GetPlaybackStateAsync(host, cancellationToken).ConfigureAwait(false);
                var info = Describe(state, describe);
                if (info == null || state == null || !state.IsActive)
                {
                    return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale));
                }

                var (title, artist, album) = info.Value;
                string what = artist == null
                    ? title
                    : album == null
                        ? ResponseStrings.Get("TrackByArtist", locale, title, artist)
                        : ResponseStrings.Get("TrackByArtistFromAlbum", locale, title, artist, album);
                return ResponseBuilder.Tell(ResponseStrings.Get("NowPlaying", locale, what));
            }

            default:
                return null;
        }
    }

    private static (string Title, string? Artist, string? Album)? Describe(SonosPlaybackState? state, Func<Guid, (string Title, string? Artist, string? Album)?> describe)
        => state != null && SonosQueueCaster.TryParseJellyfinAudioItem(state.TrackUri, out Guid itemId) ? describe(itemId) : null;
}
