namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// A snapshot of what a Sonos speaker is playing, read over UPnP.
/// </summary>
/// <param name="TransportState">The AVTransport state: <c>PLAYING</c>, <c>PAUSED_PLAYBACK</c>, <c>STOPPED</c>, <c>TRANSITIONING</c>.</param>
/// <param name="TrackUri">The current track URI (decoded), or null when the queue is empty.</param>
/// <param name="TrackNumber">The 1-based queue position of the current track (0 when none).</param>
/// <param name="PositionTicks">The position inside the current track.</param>
/// <param name="DurationTicks">The current track's duration as the speaker knows it (0 when unknown).</param>
public sealed record SonosPlaybackState(string TransportState, string? TrackUri, int TrackNumber, long PositionTicks, long DurationTicks)
{
    /// <summary>
    /// Gets a value indicating whether the speaker is paused.
    /// </summary>
    public bool IsPaused => TransportState == "PAUSED_PLAYBACK";

    /// <summary>
    /// Gets a value indicating whether the speaker is playing or about to (transitioning).
    /// </summary>
    public bool IsActive => TransportState is "PLAYING" or "TRANSITIONING" or "PAUSED_PLAYBACK";
}
