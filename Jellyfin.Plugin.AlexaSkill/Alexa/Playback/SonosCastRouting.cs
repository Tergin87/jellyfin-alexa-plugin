using System;
using System.Collections.Generic;
using System.Linq;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Configuration;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Decides when a play response is diverted to a Sonos speaker's native queue instead of
/// the Alexa AudioPlayer, and reshapes the response once the cast succeeded. Pure
/// functions; the UPnP work lives in <see cref="SonosQueueCaster"/>.
/// </summary>
public static class SonosCastRouting
{
    /// <summary>
    /// Whether requests from a device are cast to the configured Sonos speaker: a host is
    /// set and the device is either a learned split-endpoint speaker or listed explicitly.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="deviceId">The request's device id.</param>
    /// <returns>True when the device's plays go to the Sonos queue.</returns>
    public static bool ShouldCast(PluginConfiguration config, string? deviceId)
    {
        if (config == null || string.IsNullOrWhiteSpace(config.SonosCastHost) || string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        if (SplitEndpointCompatibility.IsKnown(config.SplitEndpointDeviceIds, deviceId))
        {
            return true;
        }

        return ExplicitDeviceIds(config.SonosCastDeviceIds).Contains(deviceId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Finds the fresh launch a response carries: an AudioPlayer.Play with REPLACE_ALL.
    /// Enqueues and resumes are not cast (the speaker's own queue chains tracks itself).
    /// </summary>
    /// <param name="response">The handler's response.</param>
    /// <returns>The launch directive, or null when the response carries none.</returns>
    public static AudioPlayerPlayDirective? FindLaunch(SkillResponse response)
    {
        return response?.Response?.Directives?
            .OfType<AudioPlayerPlayDirective>()
            .FirstOrDefault(d => d.PlayBehavior == PlayBehavior.ReplaceAll && d.AudioItem?.Stream?.Url != null);
    }

    /// <summary>
    /// Turns the handler's launch response into the spoken confirmation of a cast: every
    /// directive is dropped (the speaker plays, not Alexa), the session ends, and the
    /// card, if any, is kept.
    /// </summary>
    /// <param name="response">The handler's response (mutated).</param>
    /// <param name="speech">The confirmation line.</param>
    public static void ToCastConfirmation(SkillResponse response, string speech)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Response ??= new ResponseBody();
        response.Response.Directives?.Clear();
        response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = speech };
        response.Response.Reprompt = null;
        response.Response.ShouldEndSession = true;
        response.SessionAttributes = null;
    }

    /// <summary>
    /// Builds the play order from the device queue: the current item first, then the rest
    /// of the queue; the launch item alone when no queue exists or it does not contain it.
    /// </summary>
    /// <param name="queue">The device queue, if any.</param>
    /// <param name="launchItemId">The item id of the launch directive's stream.</param>
    /// <returns>The ordered item ids, starting with the launch item.</returns>
    public static IReadOnlyList<string> PlayOrder(DeviceQueue? queue, string launchItemId)
    {
        if (queue?.ItemIds == null || queue.ItemIds.Count == 0)
        {
            return new[] { launchItemId };
        }

        // Queue ids and stream tokens may differ in GUID format ("N" vs dashed): compare the parsed value.
        int index = queue.ItemIds.FindIndex(id => SameItem(id, launchItemId));
        if (index < 0)
        {
            return new[] { launchItemId };
        }

        return queue.ItemIds.Skip(index).ToList();
    }

    private static bool SameItem(string a, string b)
        => Guid.TryParse(a, out Guid ga) && Guid.TryParse(b, out Guid gb)
            ? ga == gb
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string[] ExplicitDeviceIds(string? csv)
        => (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
