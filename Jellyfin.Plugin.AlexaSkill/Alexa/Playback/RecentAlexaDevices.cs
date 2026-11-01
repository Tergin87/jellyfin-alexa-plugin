using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// The Alexa devices that talked to the skill since the server started, by device id:
/// when they were last seen, what they last sent, whether they have a screen, and
/// whether they were learned as split-endpoint speakers. In memory only (a restart
/// forgets them); the config page lists them so the Sonos speaker can be picked by a
/// click instead of pasting its id from the log.
/// </summary>
public static class RecentAlexaDevices
{
    /// <summary>
    /// The most devices kept; the oldest is dropped beyond it.
    /// </summary>
    public const int Capacity = 32;

    private static readonly ConcurrentDictionary<string, RecentAlexaDevice> Devices = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a request from a device.
    /// </summary>
    /// <param name="deviceId">The device id (the re-keyed one for events).</param>
    /// <param name="lastRequest">The request type or intent name.</param>
    /// <param name="hasDisplay">Whether the request declared a screen.</param>
    /// <param name="splitEndpointLearned">True when this request learned (or confirmed) the device as a split-endpoint speaker.</param>
    /// <param name="nowUtc">The clock (tests).</param>
    public static void Record(string? deviceId, string lastRequest, bool hasDisplay, bool splitEndpointLearned, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        DateTime now = nowUtc ?? DateTime.UtcNow;
        Devices.AddOrUpdate(
            deviceId,
            _ => new RecentAlexaDevice(deviceId, now, now, lastRequest, hasDisplay, splitEndpointLearned),
            (_, previous) => previous with
            {
                LastSeenUtc = now,
                LastRequest = lastRequest,
                HasDisplay = previous.HasDisplay || hasDisplay,
                SplitEndpointLearned = previous.SplitEndpointLearned || splitEndpointLearned,
            });

        if (Devices.Count > Capacity)
        {
            foreach (RecentAlexaDevice stale in Devices.Values.OrderBy(e => e.LastSeenUtc).Take(Devices.Count - Capacity))
            {
                Devices.TryRemove(stale.DeviceId, out _);
            }
        }
    }

    /// <summary>
    /// The devices seen, most recent first.
    /// </summary>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<RecentAlexaDevice> Snapshot() => Devices.Values.OrderByDescending(e => e.LastSeenUtc).ToList();

    /// <summary>
    /// Forgets every device (tests).
    /// </summary>
    internal static void Clear() => Devices.Clear();
}
