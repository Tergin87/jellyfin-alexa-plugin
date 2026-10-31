using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Follows a Sonos speaker after a cast and reports what it plays to Jellyfin as a
/// regular client would (playback start, progress, stop), so the dashboard, the play
/// counts and the played flags keep working although Alexa no longer streams the
/// audio. One polling loop per speaker; a new cast on the same speaker replaces it.
/// </summary>
/// <remarks>
/// The speaker is the ONLY source of truth here: the current item comes from the
/// track URI it reports (a Jellyfin <c>/Audio/{id}/stream</c> URL), the position from
/// its own clock. Unlike the AudioPlayer events on this device, both are exact. The
/// loop ends on its own once the speaker has been stopped or playing something else
/// for <see cref="IdleStop"/>, or after <see cref="MaxDuration"/>.
/// </remarks>
public sealed class SonosPlaybackMonitor
{
    /// <summary>
    /// The poll interval; Jellyfin clients report every 10 s, half that keeps track
    /// changes sharp without noticeable load on the speaker.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the speaker may be stopped or on foreign content before the loop ends.
    /// </summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The hard cap of one loop (a safety net against a never-idle speaker).
    /// </summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    private const int MaxConsecutiveReadFailures = 6;

    private readonly SonosQueueCaster _caster;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SonosPlaybackMonitor> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _loops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="SonosPlaybackMonitor"/> class.
    /// </summary>
    /// <param name="caster">The UPnP client (state reads).</param>
    /// <param name="sessionManager">Jellyfin's session manager (the report sink).</param>
    /// <param name="logger">The logger.</param>
    public SonosPlaybackMonitor(SonosQueueCaster caster, ISessionManager sessionManager, ILogger<SonosPlaybackMonitor> logger)
    {
        _caster = caster;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Starts (or restarts) following a speaker, reporting into the given Jellyfin session.
    /// </summary>
    /// <param name="host">The speaker's LAN host.</param>
    /// <param name="session">The Jellyfin session of the Alexa device that asked for the cast.</param>
    public void Start(string host, SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cts = new CancellationTokenSource(MaxDuration);
        if (_loops.TryGetValue(host, out CancellationTokenSource? previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _loops[host] = cts;
        _ = Task.Run(() => RunAsync(host, session, cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Whether a loop is following the speaker, i.e. it is (or was until very recently)
    /// playing content this plugin handed it. The queue intents use it to tell "the Sonos
    /// is playing our music" from "nothing is playing" when Alexa's own context says idle.
    /// </summary>
    /// <param name="host">The speaker's LAN host.</param>
    /// <returns>True while a loop runs for the host.</returns>
    public bool IsFollowing(string host) => !string.IsNullOrWhiteSpace(host) && _loops.ContainsKey(host);

    /// <summary>
    /// Stops following a speaker, if a loop is running for it.
    /// </summary>
    /// <param name="host">The speaker's LAN host.</param>
    public void Stop(string host)
    {
        if (_loops.TryRemove(host, out CancellationTokenSource? cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task RunAsync(string host, SessionInfo session, CancellationToken cancellationToken)
    {
        Guid currentItem = Guid.Empty;
        long lastPositionTicks = 0;
        DateTime idleSince = DateTime.MaxValue;
        int readFailures = 0;
        _logger.LogInformation("Sonos monitor: following {Host} for Jellyfin session {SessionId}", host, session.Id);

        try
        {
            // The first track needs a moment to start after the Play call.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                SonosPlaybackState? state = await _caster.GetPlaybackStateAsync(host, cancellationToken).ConfigureAwait(false);
                if (state == null)
                {
                    if (++readFailures >= MaxConsecutiveReadFailures)
                    {
                        _logger.LogWarning("Sonos monitor: {Host} stopped answering; ending the loop", host);
                        break;
                    }

                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                readFailures = 0;
                bool ours = SonosQueueCaster.TryParseJellyfinAudioItem(state.TrackUri, out Guid itemId) && state.IsActive;

                if (!ours)
                {
                    if (currentItem != Guid.Empty)
                    {
                        await ReportStopAsync(session, currentItem, lastPositionTicks, "speaker stopped or switched to other content").ConfigureAwait(false);
                        currentItem = Guid.Empty;
                    }

                    if (idleSince == DateTime.MaxValue)
                    {
                        idleSince = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - idleSince >= IdleStop)
                    {
                        _logger.LogInformation("Sonos monitor: {Host} idle for {Minutes} min; ending the loop", host, (int)IdleStop.TotalMinutes);
                        break;
                    }
                }
                else
                {
                    idleSince = DateTime.MaxValue;
                    if (itemId != currentItem)
                    {
                        if (currentItem != Guid.Empty)
                        {
                            // The previous track ended (or was skipped): close it at its last seen position.
                            await ReportStopAsync(session, currentItem, lastPositionTicks, "track changed").ConfigureAwait(false);
                        }

                        currentItem = itemId;
                        await ReportStartAsync(session, itemId, state).ConfigureAwait(false);
                    }
                    else
                    {
                        await ReportProgressAsync(session, itemId, state).ConfigureAwait(false);
                    }

                    lastPositionTicks = state.PositionTicks;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer cast, stopped explicitly, or past MaxDuration: nothing to report.
        }
        catch (ResourceNotFoundException)
        {
            _logger.LogInformation("Sonos monitor: Jellyfin session {SessionId} is gone; ending the loop for {Host}", session.Id, host);
        }
        finally
        {
            if (_loops.TryGetValue(host, out CancellationTokenSource? mine) && mine.Token == cancellationToken)
            {
                _loops.TryRemove(host, out _);
                mine.Dispose();
            }

            _logger.LogInformation("Sonos monitor: no longer following {Host}", host);
        }
    }

    private async Task ReportStartAsync(SessionInfo session, Guid itemId, SonosPlaybackState state)
    {
        _logger.LogInformation(
            "Sonos monitor: now playing item {ItemId} (queue track {Track}, {State}) at {Position}s",
            itemId, state.TrackNumber, state.TransportState, state.PositionTicks / TimeSpan.TicksPerSecond);
        try
        {
            await _sessionManager.OnPlaybackStart(new PlaybackStartInfo
            {
                SessionId = session.Id,
                ItemId = itemId,
                PositionTicks = state.PositionTicks,
                PlaybackStartTimeTicks = state.PositionTicks,
                IsPaused = state.IsPaused,
                CanSeek = true,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ResourceNotFoundException)
        {
            _logger.LogWarning(ex, "Sonos monitor: playback-start report failed for item {ItemId}", itemId);
        }
    }

    private async Task ReportProgressAsync(SessionInfo session, Guid itemId, SonosPlaybackState state)
    {
        try
        {
            await _sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
            {
                SessionId = session.Id,
                ItemId = itemId,
                PositionTicks = state.PositionTicks,
                IsPaused = state.IsPaused,
                CanSeek = true,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ResourceNotFoundException)
        {
            _logger.LogWarning(ex, "Sonos monitor: progress report failed for item {ItemId}", itemId);
        }
    }

    private async Task ReportStopAsync(SessionInfo session, Guid itemId, long positionTicks, string reason)
    {
        _logger.LogInformation("Sonos monitor: item {ItemId} stopped at {Position}s ({Reason})", itemId, positionTicks / TimeSpan.TicksPerSecond, reason);
        try
        {
            await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
            {
                SessionId = session.Id,
                ItemId = itemId,
                PositionTicks = positionTicks,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ResourceNotFoundException)
        {
            _logger.LogWarning(ex, "Sonos monitor: playback-stop report failed for item {ItemId}", itemId);
        }
    }
}
