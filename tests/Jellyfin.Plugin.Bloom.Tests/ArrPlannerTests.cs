using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public sealed class ArrPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(12);

    // Profile 1 is happy with 720p, profile 2 wants 4K.
    private static readonly Dictionary<int, int> Thresholds = new() { [1] = 720, [2] = 2160 };

    private static ArrSettings Settings(ArrAvoidMode avoid = ArrAvoidMode.GoodEnough, bool add = false) => new() { Avoid = avoid, AddFriendsItems = add };

    private static ArrMovie Movie(int id, int tmdb, bool monitored = true, bool hasFile = false, int profile = 1, string? imdb = null)
        => new(id, tmdb, imdb, monitored, hasFile, profile);

    private static FriendMovie Friend(int? tmdb, int resolution = 1080, string? imdb = null) => new(tmdb, imdb, "Film " + tmdb, 2020, resolution);

    private static RadarrPlan Radarr(IReadOnlyList<ArrMovie> movies, IReadOnlyList<FriendMovie> friends, ArrSettings settings, RadarrBook book, DateTime? now = null, IReadOnlySet<int>? excluded = null)
        => ArrPlanner.PlanRadarr(movies, friends, Thresholds, settings, book, excluded ?? new HashSet<int>(), now ?? Now, Grace);

    [Theory]
    [InlineData(1920, 1080, 1080)]
    [InlineData(1920, 800, 1080)]
    [InlineData(3840, 1600, 2160)]
    [InlineData(1280, 720, 720)]
    [InlineData(1280, 534, 720)]
    [InlineData(640, 360, 480)]
    [InlineData(0, 0, 0)]
    public void ResolutionFollowsArrQualities(int width, int height, int expected)
        => Assert.Equal(expected, ArrPlanner.Resolution(width, height));

    [Fact]
    public void AMovieAFriendHasIsUnmonitoredWhenTheirCopyIsGoodEnough()
    {
        var movies = new[] { Movie(1, 100), Movie(2, 200, profile: 2), Movie(3, 300, hasFile: true), Movie(4, 400, monitored: false), Movie(5, 500) };
        var friends = new[] { Friend(100), Friend(200), Friend(300), Friend(400) };

        var plan = Radarr(movies, friends, Settings(), new RadarrBook());

        // 2 wants 4K (the friend has 1080p), 3 is already downloaded, 4 isn't wanted, no friend has 5.
        Assert.Equal([1], plan.Unmonitor.Keys);
        Assert.Equal(100, plan.Unmonitor[1]);

        Assert.Equal([1, 2], Radarr(movies, friends, Settings(ArrAvoidMode.Always), new RadarrBook()).Unmonitor.Keys.Order());
        Assert.True(Radarr(movies, friends, Settings(ArrAvoidMode.Off), new RadarrBook()).IsEmpty);
    }

    [Fact]
    public void FriendsAreMatchedByImdbIdToo()
    {
        var plan = Radarr([Movie(1, 100, imdb: "tt0100")], [Friend(null, imdb: "TT0100")], Settings(), new RadarrBook());

        Assert.Equal([1], plan.Unmonitor.Keys);
    }

    [Fact]
    public void AnUnknownSizeIsNotGoodEnough()
    {
        var plan = Radarr([Movie(1, 100)], [Friend(100, resolution: 0)], Settings(), new RadarrBook());

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void WhatTheUserMonitorsAgainIsLeftAlone()
    {
        var book = new RadarrBook { Unmonitored = { [1] = 100 } };
        var friends = new[] { Friend(100) };

        var plan = Radarr([Movie(1, 100, monitored: true)], friends, Settings(), book);

        Assert.Equal([1], plan.Untag);
        Assert.Empty(book.Unmonitored);
        Assert.Contains(100, book.LeftAlone);

        // Next passes don't unmonitor it again, as long as a friend has it.
        Assert.True(Radarr([Movie(1, 100, monitored: true)], friends, Settings(), book).IsEmpty);

        // Once no friend has it, it is forgotten.
        Radarr([Movie(1, 100, monitored: true)], [], Settings(), book);
        Assert.Empty(book.LeftAlone);
    }

    [Fact]
    public void AMovieNoFriendHasAnyMoreIsMonitoredAgainAfterTheGracePeriod()
    {
        var book = new RadarrBook { Unmonitored = { [1] = 100 } };
        var movies = new[] { Movie(1, 100, monitored: false) };

        Assert.True(Radarr(movies, [], Settings(), book).IsEmpty);
        Assert.Equal(Now, book.GoneSince[1]);

        Assert.True(Radarr(movies, [], Settings(), book, Now.AddHours(11)).IsEmpty);
        Assert.Equal([1], Radarr(movies, [], Settings(), book, Now.AddHours(12)).Remonitor);

        // Back before the end: nothing happens, and the clock starts over next time.
        var back = new RadarrBook { Unmonitored = { [1] = 100 }, GoneSince = { [1] = Now } };
        Assert.True(Radarr(movies, [Friend(100)], Settings(), back, Now.AddHours(13)).IsEmpty);
        Assert.Empty(back.GoneSince);
    }

    [Fact]
    public void SettingsApplyAtOnce()
    {
        var movies = new[] { Movie(1, 100, monitored: false), Movie(2, 200, monitored: false) };
        var book = new RadarrBook { Unmonitored = { [1] = 100 }, Added = { [2] = 200 } };

        var plan = Radarr(movies, [Friend(100), Friend(200)], Settings(ArrAvoidMode.Off, add: false), book);

        Assert.Equal([1], plan.Remonitor);
        Assert.Equal([2], plan.Delete);
    }

    [Fact]
    public void ACopyNowWorseThanTheProfileWantsIsMonitoredAgainAtOnce()
    {
        var book = new RadarrBook { Unmonitored = { [1] = 100 } };

        var plan = Radarr([Movie(1, 100, monitored: false)], [Friend(100, resolution: 480)], Settings(), book);

        Assert.Equal([1], plan.Remonitor);

        // But a copy whose size is not known yet (just imported again) keeps it as it is.
        Assert.True(Radarr([Movie(1, 100, monitored: false)], [Friend(100, resolution: 0)], Settings(), book).IsEmpty);
    }

    [Fact]
    public void FriendsMoviesAreAddedOnceAndRemovedWhenGone()
    {
        var friends = new[] { Friend(100), Friend(200), Friend(300), Friend(400), Friend(null, imdb: "tt0500"), Friend(null) };
        var movies = new[] { Movie(1, 100) };
        var book = new RadarrBook { LeftAlone = { 300 } };

        var plan = Radarr(movies, friends, Settings(add: true), book, excluded: new HashSet<int> { 400 });

        // 100 is already there, the user deleted 300, 400 is on Radarr's exclusion list, the last one has no id.
        Assert.Equal(["Film 200", "Film "], plan.Add.Select(f => f.Title));
        Assert.Equal("tt0500", plan.Add[1].ImdbId);

        // Added: kept while a friend has it, removed after the grace period otherwise.
        book = new RadarrBook { Added = { [2] = 200 } };
        movies = [Movie(1, 100, hasFile: true), Movie(2, 200, monitored: false)];
        Assert.True(Radarr(movies, friends.Take(1).Concat([Friend(200)]).ToArray(), Settings(add: true), book).IsEmpty);
        Assert.True(Radarr(movies, [], Settings(add: true), book).IsEmpty);
        Assert.Equal([2], Radarr(movies, [], Settings(add: true), book, Now.AddDays(1)).Delete);
    }

    [Fact]
    public void AnAddedMovieTheUserMonitorsBecomesTheirs()
    {
        var book = new RadarrBook { Added = { [2] = 200 } };

        var plan = Radarr([Movie(2, 200, monitored: true)], [], Settings(add: true), book);

        Assert.Equal([2], plan.Untag);
        Assert.Empty(plan.Delete);
        Assert.Empty(book.Added);
    }

    [Fact]
    public void WhatTheUserDeletedIsNeverAddedBack()
    {
        var book = new RadarrBook { Added = { [2] = 200 }, Unmonitored = { [3] = 300 } };
        var friends = new[] { Friend(200), Friend(300) };

        var plan = Radarr([], friends, Settings(add: true), book);

        Assert.Empty(plan.Add);
        Assert.Empty(book.Added);
        Assert.Empty(book.Unmonitored);
        Assert.Equal([200, 300], book.LeftAlone.Order());
    }

    [Fact]
    public void AnIdRecordedForAnotherMovieIsForgotten()
    {
        // Another Radarr behind the same address: id 1 is now another movie, which is not Bloom's to change.
        var book = new RadarrBook { Unmonitored = { [1] = 999 } };

        var plan = Radarr([Movie(1, 100, monitored: false)], [], Settings(), book, Now.AddDays(2));

        Assert.True(plan.IsEmpty);
        Assert.Empty(book.Unmonitored);
    }

    private static ArrSeries Series(int id, int tvdb, bool monitored = true, int files = 0, int profile = 1) => new(id, tvdb, null, null, monitored, files, profile);

    private static FriendSeries FriendShow(int tvdb, params (int Season, int Episode)[] episodes)
    {
        var show = new FriendSeries { TvdbId = tvdb, Title = "Show " + tvdb };
        foreach (var e in episodes)
        {
            show.Episodes[e] = 1080;
        }

        return show;
    }

    private static ArrEpisode Ep(int id, int season, int episode, bool monitored = true, bool hasFile = false) => new(id, season, episode, monitored, hasFile);

    private static SonarrPlan Sonarr(IReadOnlyList<ArrSeries> series, Dictionary<int, IReadOnlyList<ArrEpisode>> episodes, IReadOnlyList<FriendSeries> friends, ArrSettings settings, SonarrBook book, DateTime? now = null)
        => ArrPlanner.PlanSonarr(series, episodes, friends, Thresholds, settings, book, new HashSet<int>(), now ?? Now, Grace);

    [Fact]
    public void EpisodesAFriendHasAreUnmonitoredAndTheSeriesTagged()
    {
        var series = new[] { Series(1, 81189) };
        var friends = new[] { FriendShow(81189, (1, 1), (1, 2), (1, 4)) };
        var book = new SonarrBook();
        Assert.Equal([1], ArrPlanner.SeriesToInspect(series, friends, book));

        var episodes = new Dictionary<int, IReadOnlyList<ArrEpisode>> { [1] = [Ep(10, 1, 1), Ep(11, 1, 2, hasFile: true), Ep(12, 1, 3), Ep(13, 1, 4, monitored: false)] };
        var plan = Sonarr(series, episodes, friends, Settings(), book);

        Assert.Equal([10], plan.Unmonitor.Keys);
        Assert.Equal(81189, plan.Unmonitor[10].TvdbId);
        Assert.Equal([1], plan.Tag);

        // An unmonitored series: Sonarr downloads nothing anyway.
        Assert.True(Sonarr([Series(1, 81189, monitored: false)], episodes, friends, Settings(), new SonarrBook()).IsEmpty);
    }

    [Fact]
    public void EpisodesComeBackAfterTheGracePeriodAndTheTagGoes()
    {
        var series = new[] { Series(1, 81189) };
        var book = new SonarrBook
        {
            Unmonitored = { [10] = new EpisodeRef { SeriesId = 1, TvdbId = 81189, Season = 1, Episode = 1 } },
            Tagged = { 1 }
        };
        Assert.Equal([1], ArrPlanner.SeriesToInspect(series, [], book));
        var episodes = new Dictionary<int, IReadOnlyList<ArrEpisode>> { [1] = [Ep(10, 1, 1, monitored: false)] };

        Assert.True(Sonarr(series, episodes, [], Settings(), book).IsEmpty);
        var plan = Sonarr(series, episodes, [], Settings(), book, Now.AddHours(12));

        Assert.Equal([10], plan.Remonitor);
        Assert.Equal([1], plan.Untag);
    }

    [Fact]
    public void AnEpisodeTheUserMonitorsAgainIsLeftAlone()
    {
        var series = new[] { Series(1, 81189) };
        var friends = new[] { FriendShow(81189, (1, 1)) };
        var book = new SonarrBook
        {
            Unmonitored = { [10] = new EpisodeRef { SeriesId = 1, TvdbId = 81189, Season = 1, Episode = 1 } },
            Tagged = { 1 }
        };
        var episodes = new Dictionary<int, IReadOnlyList<ArrEpisode>> { [1] = [Ep(10, 1, 1, monitored: true)] };

        var plan = Sonarr(series, episodes, friends, Settings(), book);

        Assert.Empty(plan.Unmonitor);
        Assert.Empty(plan.Remonitor);
        Assert.Equal([1], plan.Untag);
        Assert.Equal(1, book.LeftAloneEpisodes[10]);
        Assert.True(Sonarr(series, episodes, friends, Settings(), book).Unmonitor.Count == 0);
    }

    [Fact]
    public void FriendsSeriesAreAddedAndRemovedWhenGone()
    {
        var friends = new[] { FriendShow(81189, (1, 1)), FriendShow(121361, (1, 1)) };
        var book = new SonarrBook { LeftAloneSeries = { 121361 } };

        var plan = Sonarr([], [], friends, Settings(add: true), book);

        Assert.Equal([81189], plan.Add.Select(f => f.TvdbId!.Value));

        book = new SonarrBook { Added = { [5] = 81189 }, Tagged = { 5 } };
        var series = new[] { Series(5, 81189, monitored: false) };
        Assert.True(Sonarr(series, [], friends.Take(1).ToArray(), Settings(add: true), book).IsEmpty);
        Assert.True(Sonarr(series, [], [], Settings(add: true), book).IsEmpty);
        var gone = Sonarr(series, [], [], Settings(add: true), book, Now.AddHours(12));
        Assert.Equal([5], gone.Delete);
        Assert.Empty(gone.Untag);

        // Monitored by the user (or with files): theirs now.
        book = new SonarrBook { Added = { [5] = 81189 }, Tagged = { 5 } };
        var taken = Sonarr([Series(5, 81189, monitored: true)], [], friends, Settings(add: true), book);
        Assert.Empty(taken.Delete);
        Assert.Equal([5], taken.Untag);
        Assert.Contains(81189, book.LeftAloneSeries);
    }

    [Fact]
    public void CopiesOfTheSameMovieCountOnceWithTheBestOne()
    {
        var merged = ArrPlanner.MergeMovies([
            new FriendMovie(100, "tt0100", "Dune", 2021, 720),        // Alice
            new FriendMovie(null, "TT0100", "Dune", null, 2160),      // Carol: known by IMDb only, in 4K
            new FriendMovie(200, null, "Sintel", 2010, 1080),
            new FriendMovie(200, "tt0200", "Sintel", 2010, 480),
            new FriendMovie(null, "tt0200", "Sintel", 2010, 720)      // linked through the copy above
        ]);

        Assert.Equal(2, merged.Count);
        var dune = merged.Single(m => m.Title == "Dune");
        Assert.Equal(100, dune.TmdbId);
        Assert.Equal("tt0100", dune.ImdbId, ignoreCase: true);
        Assert.Equal(2160, dune.Resolution);
        Assert.Equal(2021, dune.Year);
        var sintel = merged.Single(m => m.Title == "Sintel");
        Assert.Equal(200, sintel.TmdbId);
        Assert.Equal("tt0200", sintel.ImdbId);
        Assert.Equal(1080, sintel.Resolution);
    }

    [Fact]
    public void TheBestCopyDecidesEvenWithoutAnIdInCommon()
    {
        // Alice's copy is known by TMDB only, Carol's 4K one by IMDb only: Radarr knows both ids.
        var friends = new[] { Friend(100, resolution: 720), Friend(null, resolution: 2160, imdb: "tt0100") };

        var plan = Radarr([Movie(1, 100, profile: 2, imdb: "tt0100")], friends, Settings(), new RadarrBook());

        Assert.Equal([1], plan.Unmonitor.Keys);
    }

    [Fact]
    public void TwoCopiesOfAFriendsMovieAreAddedOnce()
    {
        var plan = Radarr([], [Friend(300, imdb: "tt0300"), Friend(300, resolution: 2160)], Settings(add: true), new RadarrBook());

        var added = Assert.Single(plan.Add);
        Assert.Equal(2160, added.Resolution);
        Assert.Equal("tt0300", added.ImdbId);
    }

    [Fact]
    public void EpisodesOfEveryCopyOfASeriesAddUp()
    {
        var alice = new FriendSeries { TvdbId = 81189, TmdbId = 1396, Title = "Breaking Bad" };
        alice.Episodes[(1, 1)] = 720;
        alice.Episodes[(1, 2)] = 720;
        var carol = new FriendSeries { TmdbId = 1396, Title = "Breaking Bad" };
        carol.Episodes[(1, 2)] = 2160;
        carol.Episodes[(2, 1)] = 1080;

        var merged = Assert.Single(ArrPlanner.MergeSeries([alice, carol]));
        Assert.Equal(81189, merged.TvdbId);
        Assert.Equal(new Dictionary<(int, int), int> { [(1, 1)] = 720, [(1, 2)] = 2160, [(2, 1)] = 1080 }, merged.Episodes);

        // In Sonarr, every episode either of them has is left alone.
        var series = new[] { new ArrSeries(1, 81189, 1396, null, true, 0, 1) };
        var episodes = new Dictionary<int, IReadOnlyList<ArrEpisode>> { [1] = [Ep(10, 1, 1), Ep(11, 1, 2), Ep(12, 1, 3), Ep(20, 2, 1)] };
        Assert.Equal([10, 11, 20], Sonarr(series, episodes, [alice, carol], Settings(), new SonarrBook()).Unmonitor.Keys.Order());

        // A copy with no id in common with them, but that Sonarr knows by its IMDb id, counts too.
        var dave = new FriendSeries { ImdbId = "tt0903747", Title = "Breaking Bad" };
        dave.Episodes[(1, 3)] = 1080;
        var known = new[] { new ArrSeries(1, 81189, 1396, "tt0903747", true, 0, 1) };
        Assert.Equal([1], ArrPlanner.SeriesToInspect(known, [alice, carol, dave], new SonarrBook()));
        Assert.Equal([10, 11, 12, 20], Sonarr(known, episodes, [alice, carol, dave], Settings(), new SonarrBook()).Unmonitor.Keys.Order());
    }

    [Fact]
    public void QualityProfilesGiveTheResolutionTheyAreHappyWith()
    {
        static ArrQualityItem Q(int id, int resolution, bool allowed = true) => new() { Quality = new ArrQuality { Id = id, Resolution = resolution }, Items = [], Allowed = allowed };

        // No upgrades: the lowest quality it accepts.
        var hd = new ArrQualityProfile { Cutoff = 7, Items = [Q(1, 480, false), Q(4, 720, false), Q(9, 1080), Q(7, 1080), Q(31, 2160, false)] };
        Assert.Equal(1080, hd.Threshold());

        // Upgrades: the cutoff, a single quality or a group.
        var upgrades = new ArrQualityProfile { Cutoff = 1001, UpgradeAllowed = true, Items = [Q(4, 720), new ArrQualityItem { Id = 1001, Items = [Q(3, 1080), Q(15, 1080)], Allowed = true }, Q(31, 2160)] };
        Assert.Equal(1080, upgrades.Threshold());
        upgrades.Cutoff = 31;
        Assert.Equal(2160, upgrades.Threshold());
        upgrades.Cutoff = 12345;
        Assert.Equal(0, upgrades.Threshold());
    }

    [Theory]
    [InlineData("http://radarr:7878", "http://radarr:7878")]
    [InlineData(" http://radarr:7878/ ", "http://radarr:7878")]
    [InlineData("https://home.example/radarr/", "https://home.example/radarr")]
    [InlineData("radarr:7878", null)]
    [InlineData("ftp://radarr", null)]
    [InlineData("http://user:pass@radarr:7878", null)]
    [InlineData("http://radarr:7878/?apikey=x", null)]
    [InlineData("", null)]
    public void AddressesAreChecked(string url, string? expected)
        => Assert.Equal(expected, ArrClient.NormalizeUrl(url));
}
