using System;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// One Alexa device seen by the skill since the server started (see <see cref="RecentAlexaDevices"/>).
/// </summary>
/// <param name="DeviceId">The Alexa device id.</param>
/// <param name="FirstSeenUtc">When it was first seen since the server started.</param>
/// <param name="LastSeenUtc">When it was last seen.</param>
/// <param name="LastRequest">The last request type or intent name.</param>
/// <param name="HasDisplay">Whether the device declared a screen (Display or APL interface).</param>
/// <param name="SplitEndpointLearned">Whether the device was learned as a split-endpoint speaker (voice and audio events on two ids).</param>
public sealed record RecentAlexaDevice(string DeviceId, DateTime FirstSeenUtc, DateTime LastSeenUtc, string LastRequest, bool HasDisplay, bool SplitEndpointLearned);
