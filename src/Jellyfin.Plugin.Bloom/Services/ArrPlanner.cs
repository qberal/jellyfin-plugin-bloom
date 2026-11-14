using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Bloom.Models;

namespace Jellyfin.Plugin.Bloom.Services;

#pragma warning disable SA1402 // Small records grouped on purpose.

/// <summary>A movie some friend shares with us (the best copy if several do).</summary>
/// <param name="TmdbId">TMDB id.</param>
/// <param name="ImdbId">IMDb id.</param>
/// <param name="Title">Title.</param>
/// <param name="Year">Year.</param>
/// <param name="Resolution">Vertical resolution class of the best copy (2160, 1080, 720, 480), 0 if unknown.</param>
public sealed record FriendMovie(int? TmdbId, string? ImdbId, string Title, int? Year, int Resolution);

/// <summary>A series some friend shares with us, with the episodes they have.</summary>
public sealed class FriendSeries
{
    /// <summary>Gets or sets the TVDB id.</summary>
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets the episodes friends have: (season, episode) → resolution class of the best copy.</summary>
    public Dictionary<(int Season, int Episode), int> Episodes { get; } = [];
}

/// <summary>A movie in Radarr.</summary>
/// <param name="Id">Radarr id.</param>
/// <param name="TmdbId">TMDB id.</param>
/// <param name="ImdbId">IMDb id.</param>
/// <param name="Monitored">Whether Radarr looks for it.</param>
/// <param name="HasFile">Whether Radarr has a file for it.</param>
/// <param name="QualityProfileId">Its quality profile.</param>
public sealed record ArrMovie(int Id, int TmdbId, string? ImdbId, bool Monitored, bool HasFile, int QualityProfileId);

/// <summary>A series in Sonarr.</summary>
/// <param name="Id">Sonarr id.</param>
/// <param name="TvdbId">TVDB id.</param>
/// <param name="TmdbId">TMDB id.</param>
/// <param name="ImdbId">IMDb id.</param>
/// <param name="Monitored">Whether Sonarr looks for its episodes.</param>
/// <param name="EpisodeFileCount">Number of episode files Sonarr has.</param>
/// <param name="QualityProfileId">Its quality profile.</param>
public sealed record ArrSeries(int Id, int TvdbId, int? TmdbId, string? ImdbId, bool Monitored, int EpisodeFileCount, int QualityProfileId);

/// <summary>An episode in Sonarr.</summary>
/// <param name="Id">Sonarr episode id.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Monitored">Whether Sonarr looks for it.</param>
/// <param name="HasFile">Whether Sonarr has a file for it.</param>
public sealed record ArrEpisode(int Id, int Season, int Episode, bool Monitored, bool HasFile);

/// <summary>What to change in Radarr. The book is updated by whoever applies it, once each change went through.</summary>
public sealed class RadarrPlan
{
    /// <summary>Gets the movies to unmonitor and tag: Radarr id → TMDB id.</summary>
    public Dictionary<int, int> Unmonitor { get; } = [];

    /// <summary>Gets the movies Bloom unmonitored to monitor again (untagged, then searched).</summary>
    public List<int> Remonitor { get; } = [];

    /// <summary>Gets the movies the user took over: only Bloom's tag is removed.</summary>
    public List<int> Untag { get; } = [];

    /// <summary>Gets the movies Bloom added that must go (no friend has them any more).</summary>
    public List<int> Delete { get; } = [];

    /// <summary>Gets the friends' movies to add, unmonitored.</summary>
    public List<FriendMovie> Add { get; } = [];

    /// <summary>Gets a value indicating whether there is nothing to do.</summary>
    public bool IsEmpty => Unmonitor.Count == 0 && Remonitor.Count == 0 && Untag.Count == 0 && Delete.Count == 0 && Add.Count == 0;
}

/// <summary>What to change in Sonarr. The book is updated by whoever applies it, once each change went through.</summary>
public sealed class SonarrPlan
{
    /// <summary>Gets the episodes to unmonitor, by Sonarr episode id.</summary>
    public Dictionary<int, EpisodeRef> Unmonitor { get; } = [];

    /// <summary>Gets the episodes Bloom unmonitored to monitor again (then searched).</summary>
    public List<int> Remonitor { get; } = [];

    /// <summary>Gets the series that get Bloom's tag.</summary>
    public List<int> Tag { get; } = [];

    /// <summary>Gets the series that lose Bloom's tag.</summary>
    public List<int> Untag { get; } = [];

    /// <summary>Gets the series Bloom added that must go (no friend has them any more).</summary>
    public List<int> Delete { get; } = [];

    /// <summary>Gets the friends' series to add, unmonitored.</summary>
    public List<FriendSeries> Add { get; } = [];

    /// <summary>Gets a value indicating whether there is nothing to do.</summary>
    public bool IsEmpty => Unmonitor.Count == 0 && Remonitor.Count == 0 && Tag.Count == 0 && Untag.Count == 0 && Delete.Count == 0 && Add.Count == 0;
}

/// <summary>
/// Decides what to change in Radarr and Sonarr so they know what friends already have. Pure logic: the caller fetches
/// their state and applies the plan. Bloom only ever undoes its own changes, and never fights the user: whatever they
/// monitored again by hand (or deleted) is left alone.
/// </summary>
public static class ArrPlanner
{
    /// <summary>
    /// Gets the resolution class of a video, as Radarr and Sonarr count it.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>2160, 1080, 720 or 480, or 0 when unknown.</returns>
    public static int Resolution(int width, int height)
        => width >= 3200 || height >= 1900 ? 2160
            : width >= 1800 || height >= 1000 ? 1080
            : width >= 1200 || height >= 700 ? 720
            : width > 0 || height > 0 ? 480
            : 0;

    /// <summary>
    /// Whether Radarr or Sonarr should leave a friend's copy alone instead of downloading their own.
    /// </summary>
    /// <param name="mode">The admin's choice.</param>
    /// <param name="friendResolution">Resolution class of the friend's copy (0: unknown).</param>
    /// <param name="threshold">Lowest resolution the quality profile is happy with (0: unknown).</param>
    /// <returns>Whether to leave it.</returns>
    public static bool Avoids(ArrAvoidMode mode, int friendResolution, int threshold) => mode switch
    {
        ArrAvoidMode.Always => true,
        ArrAvoidMode.GoodEnough => friendResolution > 0 ? friendResolution >= threshold : threshold == 0,
        _ => false
    };

    /// <summary>
    /// Merges the copies of the same movie (several friends, several libraries): copies sharing an id, directly or
    /// through another copy, are one movie, with every id they have and the best resolution.
    /// </summary>
    /// <param name="copies">Every copy.</param>
    /// <returns>One entry per movie.</returns>
    public static List<FriendMovie> MergeMovies(IEnumerable<FriendMovie> copies)
        => Group(copies.ToList(), MovieKeys).Select(CombineMovies).ToList();

    /// <summary>
    /// Merges the copies of the same series: copies sharing an id, directly or through another copy, are one series,
    /// with every id they have and every episode any of them has (each with its best resolution).
    /// </summary>
    /// <param name="copies">Every copy.</param>
    /// <returns>One entry per series.</returns>
    public static List<FriendSeries> MergeSeries(IEnumerable<FriendSeries> copies)
        => Group(copies.ToList(), SeriesKeys).Select(CombineSeries).ToList();

    /// <summary>
    /// Plans the changes in Radarr. Updates the book only for what needs no call (forgetting what the user took over).
    /// </summary>
    /// <param name="movies">Every movie in Radarr.</param>
    /// <param name="friends">Every movie friends share.</param>
    /// <param name="thresholds">Quality profile id → lowest resolution it is happy with.</param>
    /// <param name="settings">The admin's choices.</param>
    /// <param name="book">What Bloom changed so far.</param>
    /// <param name="excluded">TMDB ids on Radarr's exclusion list (never added).</param>
    /// <param name="now">Now.</param>
    /// <param name="grace">How long a movie must be gone from every friend before Bloom undoes its change.</param>
    /// <returns>The plan.</returns>
    public static RadarrPlan PlanRadarr(
        IReadOnlyList<ArrMovie> movies,
        IReadOnlyList<FriendMovie> friends,
        IReadOnlyDictionary<int, int> thresholds,
        ArrSettings settings,
        RadarrBook book,
        IReadOnlySet<int> excluded,
        DateTime now,
        TimeSpan grace)
    {
        var plan = new RadarrPlan();
        friends = MergeMovies(friends);
        var byTmdb = new Dictionary<int, FriendMovie>();
        var byImdb = new Dictionary<string, FriendMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var friend in friends)
        {
            if (friend.TmdbId is int tmdb)
            {
                byTmdb.TryAdd(tmdb, friend);
            }

            if (!string.IsNullOrEmpty(friend.ImdbId))
            {
                byImdb.TryAdd(friend.ImdbId, friend);
            }
        }

        // Copies with no id in common can still be the same Radarr movie (one known by TMDB, one by IMDb): the best wins.
        FriendMovie? Match(ArrMovie m)
        {
            var byTmdbId = byTmdb.GetValueOrDefault(m.TmdbId);
            var byImdbId = m.ImdbId is string imdb ? byImdb.GetValueOrDefault(imdb) : null;
            return byTmdbId is null || (byImdbId is not null && byImdbId.Resolution > byTmdbId.Resolution) ? byImdbId : byTmdbId;
        }
        int Threshold(ArrMovie m) => thresholds.GetValueOrDefault(m.QualityProfileId);

        foreach (var m in movies)
        {
            var friend = Match(m);

            // An id recorded for another movie (another Radarr behind the same address): not ours.
            if (book.Unmonitored.TryGetValue(m.Id, out var recorded) && recorded != m.TmdbId)
            {
                book.Unmonitored.Remove(m.Id);
            }

            if (book.Added.TryGetValue(m.Id, out recorded) && recorded != m.TmdbId)
            {
                book.Added.Remove(m.Id);
            }

            var added = book.Added.ContainsKey(m.Id);
            if (added || book.Unmonitored.ContainsKey(m.Id))
            {
                if (m.Monitored || m.HasFile)
                {
                    // The user took it over (or downloaded it): Bloom lets go.
                    book.Added.Remove(m.Id);
                    book.Unmonitored.Remove(m.Id);
                    book.GoneSince.Remove(m.Id);
                    plan.Untag.Add(m.Id);
                    if (friend is not null)
                    {
                        book.LeftAlone.Add(m.TmdbId);
                    }

                    continue;
                }

                // A copy whose size Jellyfin does not know yet (just imported) is kept as it was.
                var keep = added
                    ? friend is not null && settings.AddFriendsItems
                    : friend is not null && settings.Avoid != ArrAvoidMode.Off && (friend.Resolution == 0 || Avoids(settings.Avoid, friend.Resolution, Threshold(m)));
                if (keep)
                {
                    book.GoneSince.Remove(m.Id);
                    continue;
                }

                // A friend's copy that disappears may come back (folder moved, friend paused for a moment): wait. What
                // the admin changed in the settings, or a copy now worse than the profile wants, applies at once.
                var becauseOfSettings = added ? !settings.AddFriendsItems : settings.Avoid == ArrAvoidMode.Off || friend is not null;
                if (!becauseOfSettings && !GraceOver(book.GoneSince, m.Id, now, grace))
                {
                    continue;
                }

                (added ? plan.Delete : plan.Remonitor).Add(m.Id);
                continue;
            }

            if (book.LeftAlone.Contains(m.TmdbId))
            {
                continue;
            }

            if (friend is not null && m.Monitored && !m.HasFile && Avoids(settings.Avoid, friend.Resolution, Threshold(m)))
            {
                plan.Unmonitor[m.Id] = m.TmdbId;
            }
        }

        // What Bloom recorded but is no longer in Radarr was deleted by the user: never add it back.
        var present = movies.Select(m => m.Id).ToHashSet();
        foreach (var (id, tmdb) in book.Added.Concat(book.Unmonitored).Where(e => !present.Contains(e.Key)).ToList())
        {
            book.Added.Remove(id);
            book.Unmonitored.Remove(id);
            book.GoneSince.Remove(id);
            if (byTmdb.ContainsKey(tmdb))
            {
                book.LeftAlone.Add(tmdb);
            }
        }

        foreach (var id in book.GoneSince.Keys.Where(id => !present.Contains(id)).ToList())
        {
            book.GoneSince.Remove(id);
        }

        // Once no friend has it, a movie the user took over is just theirs: forget it.
        book.LeftAlone.RemoveWhere(tmdb => !byTmdb.ContainsKey(tmdb));

        if (settings.AddFriendsItems)
        {
            var inRadarr = movies.Select(m => m.TmdbId).ToHashSet();
            var inRadarrImdb = movies.Where(m => !string.IsNullOrEmpty(m.ImdbId)).Select(m => m.ImdbId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in friends)
            {
                if ((friend.TmdbId is int tmdb && (inRadarr.Contains(tmdb) || book.LeftAlone.Contains(tmdb) || excluded.Contains(tmdb)))
                    || (friend.ImdbId is string imdb && inRadarrImdb.Contains(imdb))
                    || (friend.TmdbId is null && string.IsNullOrEmpty(friend.ImdbId)))
                {
                    continue;
                }

                plan.Add.Add(friend);
            }
        }

        return plan;
    }

    /// <summary>
    /// The series whose episodes Bloom must read to plan: those friends share, and those it changed before.
    /// </summary>
    /// <param name="series">Every series in Sonarr.</param>
    /// <param name="friends">Every series friends share.</param>
    /// <param name="book">What Bloom changed so far.</param>
    /// <returns>Sonarr series ids.</returns>
    public static HashSet<int> SeriesToInspect(IReadOnlyList<ArrSeries> series, IReadOnlyList<FriendSeries> friends, SonarrBook book)
    {
        var match = SeriesMatcher(MergeSeries(friends));
        var ids = series.Where(s => match(s) is not null).Select(s => s.Id).ToHashSet();
        ids.UnionWith(book.Unmonitored.Values.Select(r => r.SeriesId));
        ids.UnionWith(book.LeftAloneEpisodes.Values);
        ids.IntersectWith(series.Select(s => s.Id));
        return ids;
    }

    /// <summary>
    /// Plans the changes in Sonarr. Updates the book only for what needs no call (forgetting what the user took over).
    /// </summary>
    /// <param name="series">Every series in Sonarr.</param>
    /// <param name="episodes">Episodes of the series returned by <see cref="SeriesToInspect"/>.</param>
    /// <param name="friends">Every series friends share.</param>
    /// <param name="thresholds">Quality profile id → lowest resolution it is happy with.</param>
    /// <param name="settings">The admin's choices.</param>
    /// <param name="book">What Bloom changed so far.</param>
    /// <param name="excluded">TVDB ids on Sonarr's exclusion list (never added).</param>
    /// <param name="now">Now.</param>
    /// <param name="grace">How long an episode must be gone from every friend before Bloom undoes its change.</param>
    /// <returns>The plan.</returns>
    public static SonarrPlan PlanSonarr(
        IReadOnlyList<ArrSeries> series,
        IReadOnlyDictionary<int, IReadOnlyList<ArrEpisode>> episodes,
        IReadOnlyList<FriendSeries> friends,
        IReadOnlyDictionary<int, int> thresholds,
        ArrSettings settings,
        SonarrBook book,
        IReadOnlySet<int> excluded,
        DateTime now,
        TimeSpan grace)
    {
        var plan = new SonarrPlan();
        friends = MergeSeries(friends);
        var match = SeriesMatcher(friends);
        var present = series.Select(s => s.Id).ToHashSet();

        foreach (var s in series)
        {
            var friend = match(s);
            var threshold = thresholds.GetValueOrDefault(s.QualityProfileId);
            if (episodes.TryGetValue(s.Id, out var list))
            {
                PlanEpisodes(s, list, friend, threshold, settings, book, plan, now, grace);
            }

            if (book.Added.TryGetValue(s.Id, out var tvdb) && tvdb != s.TvdbId)
            {
                book.Added.Remove(s.Id);
            }

            if (book.Added.ContainsKey(s.Id))
            {
                if (s.Monitored || s.EpisodeFileCount > 0)
                {
                    book.Added.Remove(s.Id);
                    book.SeriesGoneSince.Remove(s.Id);
                    if (friend is not null)
                    {
                        book.LeftAloneSeries.Add(s.TvdbId);
                    }
                }
                else if (friend is not null && settings.AddFriendsItems)
                {
                    book.SeriesGoneSince.Remove(s.Id);
                }
                else if (!settings.AddFriendsItems || GraceOver(book.SeriesGoneSince, s.Id, now, grace))
                {
                    plan.Delete.Add(s.Id);
                }
            }
            else if (friend is null)
            {
                book.LeftAloneSeries.Remove(s.TvdbId);
            }
        }

        // Series deleted by the user: forget what Bloom did in them, and never add them back.
        foreach (var (id, tvdb) in book.Added.Where(e => !present.Contains(e.Key)).ToList())
        {
            book.Added.Remove(id);
            if (friends.Any(f => f.TvdbId == tvdb))
            {
                book.LeftAloneSeries.Add(tvdb);
            }
        }

        foreach (var id in book.Unmonitored.Where(e => !present.Contains(e.Value.SeriesId)).Select(e => e.Key).ToList())
        {
            book.Unmonitored.Remove(id);
        }

        foreach (var id in book.LeftAloneEpisodes.Where(e => !present.Contains(e.Value)).Select(e => e.Key).ToList())
        {
            book.LeftAloneEpisodes.Remove(id);
        }

        foreach (var id in book.EpisodesGoneSince.Keys.Where(id => !book.Unmonitored.ContainsKey(id)).ToList())
        {
            book.EpisodesGoneSince.Remove(id);
        }

        foreach (var id in book.SeriesGoneSince.Keys.Where(id => !book.Added.ContainsKey(id)).ToList())
        {
            book.SeriesGoneSince.Remove(id);
        }

        book.Tagged.IntersectWith(present);
        book.LeftAloneSeries.RemoveWhere(tvdb => !friends.Any(f => f.TvdbId == tvdb));

        // Bloom's tag marks the series where it did something: unmonitored episodes, or the whole series added.
        var remonitored = plan.Remonitor.ToHashSet();
        var deleted = plan.Delete.ToHashSet();
        var tagged = book.Unmonitored.Where(e => !remonitored.Contains(e.Key)).Select(e => e.Value.SeriesId)
            .Concat(plan.Unmonitor.Values.Select(r => r.SeriesId))
            .Concat(book.Added.Keys.Where(id => !deleted.Contains(id)))
            .ToHashSet();
        plan.Tag.AddRange(tagged.Where(id => present.Contains(id) && !book.Tagged.Contains(id)));
        plan.Untag.AddRange(book.Tagged.Where(id => !tagged.Contains(id) && !deleted.Contains(id)));

        if (settings.AddFriendsItems)
        {
            foreach (var friend in friends)
            {
                var inSonarr = series.Any(s => (friend.TvdbId is int t && s.TvdbId == t)
                    || (friend.TmdbId is int m && s.TmdbId == m)
                    || (!string.IsNullOrEmpty(friend.ImdbId) && string.Equals(s.ImdbId, friend.ImdbId, StringComparison.OrdinalIgnoreCase)));
                if (inSonarr
                    || (friend.TvdbId is int tvdbId && (book.LeftAloneSeries.Contains(tvdbId) || excluded.Contains(tvdbId)))
                    || (friend.TvdbId is null && friend.TmdbId is null && string.IsNullOrEmpty(friend.ImdbId)))
                {
                    continue;
                }

                plan.Add.Add(friend);
            }
        }

        return plan;
    }

    private static void PlanEpisodes(ArrSeries s, IReadOnlyList<ArrEpisode> list, FriendSeries? friend, int threshold, ArrSettings settings, SonarrBook book, SonarrPlan plan, DateTime now, TimeSpan grace)
    {
        var ids = new HashSet<int>();
        foreach (var e in list)
        {
            ids.Add(e.Id);
            int? friendResolution = friend is not null && friend.Episodes.TryGetValue((e.Season, e.Episode), out var r) ? r : null;

            if (book.Unmonitored.TryGetValue(e.Id, out var recorded)
                && (recorded.SeriesId != s.Id || recorded.TvdbId != s.TvdbId || recorded.Season != e.Season || recorded.Episode != e.Episode))
            {
                // Recorded for another episode (renumbered, or another Sonarr behind the same address).
                book.Unmonitored.Remove(e.Id);
            }

            if (book.Unmonitored.ContainsKey(e.Id))
            {
                if (e.Monitored || e.HasFile)
                {
                    book.Unmonitored.Remove(e.Id);
                    book.EpisodesGoneSince.Remove(e.Id);
                    if (friendResolution is not null)
                    {
                        book.LeftAloneEpisodes[e.Id] = s.Id;
                    }

                    continue;
                }

                if (friendResolution is int res && settings.Avoid != ArrAvoidMode.Off && (res == 0 || Avoids(settings.Avoid, res, threshold)))
                {
                    book.EpisodesGoneSince.Remove(e.Id);
                    continue;
                }

                var becauseOfSettings = settings.Avoid == ArrAvoidMode.Off || friendResolution is not null;
                if (becauseOfSettings || GraceOver(book.EpisodesGoneSince, e.Id, now, grace))
                {
                    plan.Remonitor.Add(e.Id);
                }

                continue;
            }

            if (book.LeftAloneEpisodes.ContainsKey(e.Id))
            {
                if (friendResolution is null)
                {
                    book.LeftAloneEpisodes.Remove(e.Id);
                }

                continue;
            }

            if (friendResolution is int resolution && s.Monitored && e.Monitored && !e.HasFile && Avoids(settings.Avoid, resolution, threshold))
            {
                plan.Unmonitor[e.Id] = new EpisodeRef { SeriesId = s.Id, TvdbId = s.TvdbId, Season = e.Season, Episode = e.Episode };
            }
        }

        // Episodes Sonarr no longer has (renumbered, deleted).
        foreach (var id in book.Unmonitored.Where(x => x.Value.SeriesId == s.Id && !ids.Contains(x.Key)).Select(x => x.Key).ToList())
        {
            book.Unmonitored.Remove(id);
        }

        foreach (var id in book.LeftAloneEpisodes.Where(x => x.Value == s.Id && !ids.Contains(x.Key)).Select(x => x.Key).ToList())
        {
            book.LeftAloneEpisodes.Remove(id);
        }
    }

    private static Func<ArrSeries, FriendSeries?> SeriesMatcher(IReadOnlyList<FriendSeries> friends)
    {
        var byTvdb = new Dictionary<int, FriendSeries>();
        var byTmdb = new Dictionary<int, FriendSeries>();
        var byImdb = new Dictionary<string, FriendSeries>(StringComparer.OrdinalIgnoreCase);
        foreach (var friend in friends)
        {
            if (friend.TvdbId is int tvdb)
            {
                byTvdb.TryAdd(tvdb, friend);
            }

            if (friend.TmdbId is int tmdb)
            {
                byTmdb.TryAdd(tmdb, friend);
            }

            if (!string.IsNullOrEmpty(friend.ImdbId))
            {
                byImdb.TryAdd(friend.ImdbId, friend);
            }
        }

        // Copies with no id in common can still be the same Sonarr series (one known by TVDB, one by TMDB): together.
        return s =>
        {
            var found = new[]
                {
                    byTvdb.GetValueOrDefault(s.TvdbId),
                    s.TmdbId is int tmdb ? byTmdb.GetValueOrDefault(tmdb) : null,
                    s.ImdbId is string imdb ? byImdb.GetValueOrDefault(imdb) : null
                }
                .OfType<FriendSeries>()
                .Distinct()
                .ToList();
            return found.Count switch
            {
                0 => null,
                1 => found[0],
                _ => CombineSeries(found)
            };
        };
    }

    private static IEnumerable<string> MovieKeys(FriendMovie movie)
    {
        if (movie.TmdbId is int tmdb)
        {
            yield return "tmdb:" + tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(movie.ImdbId))
        {
            yield return "imdb:" + movie.ImdbId.ToLowerInvariant();
        }
    }

    private static IEnumerable<string> SeriesKeys(FriendSeries series)
    {
        if (series.TvdbId is int tvdb)
        {
            yield return "tvdb:" + tvdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (series.TmdbId is int tmdb)
        {
            yield return "tmdb:" + tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(series.ImdbId))
        {
            yield return "imdb:" + series.ImdbId.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Groups items sharing a key, directly or through another item (union-find).
    /// </summary>
    private static IEnumerable<List<T>> Group<T>(List<T> items, Func<T, IEnumerable<string>> keys)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var key in keys(items[i]))
            {
                if (byKey.TryGetValue(key, out var other))
                {
                    parent[Find(i)] = Find(other);
                }
                else
                {
                    byKey[key] = i;
                }
            }
        }

        return Enumerable.Range(0, items.Count).GroupBy(Find).Select(g => g.Select(i => items[i]).ToList());
    }

    private static FriendMovie CombineMovies(List<FriendMovie> copies)
    {
        var best = copies.MaxBy(m => m.Resolution)!;
        return best with
        {
            TmdbId = best.TmdbId ?? copies.Select(m => m.TmdbId).FirstOrDefault(t => t is not null),
            ImdbId = !string.IsNullOrEmpty(best.ImdbId) ? best.ImdbId : copies.Select(m => m.ImdbId).FirstOrDefault(i => !string.IsNullOrEmpty(i)),
            Year = best.Year ?? copies.Select(m => m.Year).FirstOrDefault(y => y is not null)
        };
    }

    private static FriendSeries CombineSeries(List<FriendSeries> copies)
    {
        if (copies.Count == 1)
        {
            return copies[0];
        }

        var merged = new FriendSeries
        {
            TvdbId = copies.Select(s => s.TvdbId).FirstOrDefault(t => t is not null),
            TmdbId = copies.Select(s => s.TmdbId).FirstOrDefault(t => t is not null),
            ImdbId = copies.Select(s => s.ImdbId).FirstOrDefault(i => !string.IsNullOrEmpty(i)),
            Title = copies.Select(s => s.Title).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty
        };
        foreach (var copy in copies)
        {
            foreach (var (episode, resolution) in copy.Episodes)
            {
                if (!merged.Episodes.TryGetValue(episode, out var known) || known < resolution)
                {
                    merged.Episodes[episode] = resolution;
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Starts the grace period of an entry if needed, and tells whether it is over.
    /// </summary>
    private static bool GraceOver(Dictionary<int, DateTime> goneSince, int id, DateTime now, TimeSpan grace)
    {
        if (!goneSince.TryGetValue(id, out var since))
        {
            goneSince[id] = now;
            since = now;
        }

        return now - since >= grace;
    }
}
