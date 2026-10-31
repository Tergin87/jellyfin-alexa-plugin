namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// One track handed to the Sonos queue: the Jellyfin stream URL plus the metadata the
/// speaker shows in the Sonos app.
/// </summary>
/// <param name="Url">The stream URL the speaker fetches itself (must be reachable from the LAN).</param>
/// <param name="Title">The track title.</param>
/// <param name="Artist">The artist, when known.</param>
/// <param name="Album">The album, when known.</param>
/// <param name="ArtUrl">The cover art URL, when known.</param>
/// <param name="MimeType">The MIME type the DIDL resource advertises (see <see cref="SonosQueueCaster.MimeTypeForContainer"/>).</param>
public sealed record SonosTrack(string Url, string Title, string? Artist, string? Album, string? ArtUrl, string MimeType);
