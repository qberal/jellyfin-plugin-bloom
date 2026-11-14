using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Bloom.Models;

#pragma warning disable SA1402 // Small DTOs grouped on purpose.

/// <summary>What Radarr and Sonarr do with a movie or an episode a friend already has.</summary>
public enum ArrAvoidMode
{
    /// <summary>They download it anyway.</summary>
    Off = 0,

    /// <summary>They don't, unless the quality profile asks for better than the friend's copy.</summary>
    GoodEnough = 1,

    /// <summary>They never do.</summary>
    Always = 2
}

/// <summary>Address and API key of Radarr or Sonarr.</summary>
public sealed class ArrConnection
{
    /// <summary>Gets or sets the base URL (http://radarr:7878, with its URL base if any).</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the API key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the admin disconnected it: Bloom first undoes what it changed there, then
    /// forgets it.
    /// </summary>
    public bool Release { get; set; }

    /// <summary>Gets a value indicating whether Bloom may talk to it.</summary>
    [JsonIgnore]
    public bool IsSet => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>How Bloom works with Radarr and Sonarr.</summary>
public sealed class ArrSettings
{
    /// <summary>Gets or sets Radarr.</summary>
    public ArrConnection Radarr { get; set; } = new();

    /// <summary>Gets or sets Sonarr.</summary>
    public ArrConnection Sonarr { get; set; } = new();

    /// <summary>Gets or sets what they do with what a friend already has.</summary>
    public ArrAvoidMode Avoid { get; set; } = ArrAvoidMode.GoodEnough;

    /// <summary>Gets or sets a value indicating whether friends' movies and series are added to them (unmonitored).</summary>
    public bool AddFriendsItems { get; set; }
}

/// <summary>Outcome of the last pass on Radarr or Sonarr.</summary>
public sealed class ArrRunStatus
{
    /// <summary>Gets or sets when it ran.</summary>
    public DateTime? LastRun { get; set; }

    /// <summary>Gets or sets the version of Radarr or Sonarr.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the error, if it failed.</summary>
    public string? Error { get; set; }
}

/// <summary>An episode as Bloom recorded it: enough to recognize it again.</summary>
public sealed class EpisodeRef
{
    /// <summary>Gets or sets the Sonarr series id.</summary>
    public int SeriesId { get; set; }

    /// <summary>Gets or sets the TVDB id of the series.</summary>
    public int TvdbId { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int Episode { get; set; }
}

/// <summary>What Bloom changed in Radarr, to undo it when friends no longer have the movies.</summary>
public sealed class RadarrBook
{
    /// <summary>Gets or sets the movies Bloom unmonitored because a friend has them: Radarr id → TMDB id.</summary>
    public Dictionary<int, int> Unmonitored { get; set; } = [];

    /// <summary>Gets or sets the friends' movies Bloom added: Radarr id → TMDB id.</summary>
    public Dictionary<int, int> Added { get; set; } = [];

    /// <summary>Gets or sets the movies no friend has any more since then, by Radarr id (undone after a grace period).</summary>
    public Dictionary<int, DateTime> GoneSince { get; set; } = [];

    /// <summary>Gets or sets the TMDB ids of movies the user took over (monitored again, or deleted): left alone.</summary>
    public HashSet<int> LeftAlone { get; set; } = [];

    /// <summary>Gets or sets the outcome of the last pass.</summary>
    public ArrRunStatus Status { get; set; } = new();
}

/// <summary>What Bloom changed in Sonarr, to undo it when friends no longer have the episodes.</summary>
public sealed class SonarrBook
{
    /// <summary>Gets or sets the episodes Bloom unmonitored because a friend has them, by Sonarr episode id.</summary>
    public Dictionary<int, EpisodeRef> Unmonitored { get; set; } = [];

    /// <summary>Gets or sets the friends' series Bloom added: Sonarr id → TVDB id.</summary>
    public Dictionary<int, int> Added { get; set; } = [];

    /// <summary>Gets or sets the episodes no friend has any more since then, by Sonarr episode id.</summary>
    public Dictionary<int, DateTime> EpisodesGoneSince { get; set; } = [];

    /// <summary>Gets or sets the added series no friend has any more since then, by Sonarr id.</summary>
    public Dictionary<int, DateTime> SeriesGoneSince { get; set; } = [];

    /// <summary>Gets or sets the episodes the user took over (monitored again), left alone: episode id → series id.</summary>
    public Dictionary<int, int> LeftAloneEpisodes { get; set; } = [];

    /// <summary>Gets or sets the TVDB ids of series Bloom added that the user took over or deleted: left alone.</summary>
    public HashSet<int> LeftAloneSeries { get; set; } = [];

    /// <summary>Gets or sets the series Bloom put its tag on.</summary>
    public HashSet<int> Tagged { get; set; } = [];

    /// <summary>Gets or sets the outcome of the last pass.</summary>
    public ArrRunStatus Status { get; set; } = new();
}

/// <summary>What Bloom changed in Radarr and Sonarr (stored next to the state, in arr.json).</summary>
public sealed class ArrBook
{
    /// <summary>Gets or sets Radarr's part.</summary>
    public RadarrBook Radarr { get; set; } = new();

    /// <summary>Gets or sets Sonarr's part.</summary>
    public SonarrBook Sonarr { get; set; } = new();
}
