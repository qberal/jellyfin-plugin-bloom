using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Tells Radarr and Sonarr what friends already have: what a friend has is unmonitored (so it isn't downloaded
/// again), friends' movies and series can be added unmonitored (so they show as already there), and all of it is
/// undone when no friend has them any more. Runs after each library scan and each scheduled sync.
/// </summary>
public sealed class ArrSync : IDisposable
{
    /// <summary>Name of the HTTP client used for Radarr and Sonarr.</summary>
    public const string HttpClientName = "BloomArr";

    /// <summary>Tag Bloom puts on what it changed.</summary>
    public const string TagLabel = "bloom";

    private const int SaveEvery = 20;

    private readonly FederationStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly CatalogService _catalog;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ArrSync> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _queued;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrSync"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="catalog">Catalog service.</param>
    /// <param name="httpFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public ArrSync(FederationStore store, ILibraryManager libraryManager, CatalogService catalog, IHttpClientFactory httpFactory, ILogger<ArrSync> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _catalog = catalog;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets how long a movie or an episode must be gone from every friend before Bloom undoes what it did (it may
    /// come back: friends' folder moved, friend paused for a moment...). <c>BLOOM_ARR_GRACE_MINUTES</c> changes it.
    /// </summary>
    public static TimeSpan Grace { get; } = int.TryParse(Environment.GetEnvironmentVariable("BLOOM_ARR_GRACE_MINUTES"), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
        ? TimeSpan.FromMinutes(minutes)
        : TimeSpan.FromHours(12);

    /// <summary>Gets a value indicating whether a pass is running or waiting to run.</summary>
    public bool IsBusy => Volatile.Read(ref _queued) == 1 || _gate.CurrentCount == 0;

    /// <summary>
    /// Runs a pass in the background, unless one is already waiting to run.
    /// </summary>
    public void Queue()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Volatile.Write(ref _queued, 0);
                await RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bloom: Radarr/Sonarr pass failed");
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Started from the settings page or not, what is stored (errors) is in the server's language.
        Text.UseRequestLanguage(null);

        var settings = _store.Snapshot().Arr;
        if (!settings.Radarr.IsSet && !settings.Sonarr.IsSet)
        {
            return;
        }

        var (movies, series) = ReadFriendsItems();
        var book = _store.LoadArrBook();

        // Disconnected by the admin: as if nothing should be avoided nor added, which undoes everything at once.
        var undo = new ArrSettings { Avoid = ArrAvoidMode.Off, AddFriendsItems = false };
        try
        {
            if (settings.Radarr.IsSet)
            {
                var radarr = settings.Radarr.Release ? undo : settings;
                await RunAppAsync("Radarr", settings.Radarr, book.Radarr.Status, ct => RadarrAsync(Client("Radarr", settings.Radarr), radarr, movies, book, ct), cancellationToken).ConfigureAwait(false);
                if (settings.Radarr.Release && book.Radarr.Status.Error is null && book.Radarr.Unmonitored.Count == 0 && book.Radarr.Added.Count == 0)
                {
                    book.Radarr = new RadarrBook();
                    Forget(settings.Radarr, s => s.Radarr, (s, c) => s.Radarr = c);
                }
            }

            if (settings.Sonarr.IsSet)
            {
                var sonarr = settings.Sonarr.Release ? undo : settings;
                await RunAppAsync("Sonarr", settings.Sonarr, book.Sonarr.Status, ct => SonarrAsync(Client("Sonarr", settings.Sonarr), sonarr, series, book, ct), cancellationToken).ConfigureAwait(false);
                if (settings.Sonarr.Release && book.Sonarr.Status.Error is null && book.Sonarr.Unmonitored.Count == 0 && book.Sonarr.Added.Count == 0 && book.Sonarr.Tagged.Count == 0)
                {
                    book.Sonarr = new SonarrBook();
                    Forget(settings.Sonarr, s => s.Sonarr, (s, c) => s.Sonarr = c);
                }
            }
        }
        finally
        {
            _store.SaveArrBook(book);
        }
    }

    /// <summary>
    /// Forgets a connection once everything Bloom changed there was undone, unless the admin set it again meanwhile.
    /// </summary>
    private void Forget(ArrConnection released, Func<ArrSettings, ArrConnection> get, Action<ArrSettings, ArrConnection> set)
        => _store.TryUpdate(s =>
        {
            var current = get(s.Arr);
            if (!current.Release || current.Url != released.Url || current.ApiKey != released.ApiKey)
            {
                return false;
            }

            set(s.Arr, new ArrConnection());
            return true;
        });

    private async Task RunAppAsync(string app, ArrConnection connection, ArrRunStatus status, Func<CancellationToken, Task<string?>> run, CancellationToken cancellationToken)
    {
        status.LastRun = DateTime.UtcNow;
        try
        {
            status.Version = await run(cancellationToken).ConfigureAwait(false);
            status.Error = null;
        }
        catch (ArrException ex)
        {
            _logger.LogWarning("Bloom: {App} at {Url}: {Message}", app, connection.Url, ex.Message);
            status.Error = ex.Message;
        }
    }

    private ArrClient Client(string app, ArrConnection connection)
        => new(_httpFactory.CreateClient(HttpClientName), app, ArrClient.NormalizeUrl(connection.Url) ?? connection.Url!, connection.ApiKey!);

    private static async Task<string?> CheckAppAsync(ArrClient client, CancellationToken cancellationToken)
    {
        var status = await client.GetAsync<ArrSystemStatus>("/api/v3/system/status", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(status.AppName, client.App, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArrException(Text.Get("ArrWrongApp", client.App, status.AppName ?? "?"));
        }

        if (!Version.TryParse(status.Version, out var version) || version.Major < 4)
        {
            throw new ArrException(Text.Get("ArrTooOld", client.App));
        }

        return status.Version;
    }

    private static async Task<int> TagAsync(ArrClient client, CancellationToken cancellationToken)
    {
        var tags = await client.GetAsync<List<ArrTag>>("/api/v3/tag", cancellationToken).ConfigureAwait(false);
        if (tags.Find(t => string.Equals(t.Label, TagLabel, StringComparison.OrdinalIgnoreCase)) is { } tag)
        {
            return tag.Id;
        }

        var created = await client.SendAsync<ArrTag>(HttpMethod.Post, "/api/v3/tag", new { label = TagLabel }, cancellationToken).ConfigureAwait(false);
        return created?.Id ?? throw new ArrException(Text.Get("ArrUnreadable", client.App));
    }

    private static async Task<HashSet<int>> ExclusionsAsync(ArrClient client, string path, Func<ArrExclusion, int> id, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.GetAsync<List<ArrExclusion>>(path, cancellationToken).ConfigureAwait(false)).Select(id).Where(i => i > 0).ToHashSet();
        }
        catch (ArrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    /// <summary>
    /// Where to add friends' items: the root folder and quality profile most of the library uses.
    /// </summary>
    private static async Task<(string RootFolder, int Profile)> TargetAsync(ArrClient client, IEnumerable<(string? Root, int Profile)> library, IReadOnlyList<ArrQualityProfile> profiles, CancellationToken cancellationToken)
    {
        static string Trim(string? path) => (path ?? string.Empty).TrimEnd('/', '\\');
        var roots = (await client.GetAsync<List<ArrRootFolder>>("/api/v3/rootfolder", cancellationToken).ConfigureAwait(false))
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => r.Path!)
            .ToList();
        if (roots.Count == 0 || profiles.Count == 0)
        {
            throw new ArrException(Text.Get("ArrNoRootFolder", client.App));
        }

        var items = library.ToList();
        var root = items.Select(i => Trim(i.Root))
            .Where(r => roots.Any(x => Trim(x) == r))
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .Select(g => roots.First(x => Trim(x) == g.Key))
            .FirstOrDefault() ?? roots[0];
        var profile = items.Select(i => i.Profile)
            .Where(p => profiles.Any(x => x.Id == p))
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefault() ?? profiles[0].Id;
        return (root, profile);
    }

    private async Task<string?> RadarrAsync(ArrClient client, ArrSettings settings, List<FriendMovie> friends, ArrBook arrBook, CancellationToken cancellationToken)
    {
        var book = arrBook.Radarr;
        var version = await CheckAppAsync(client, cancellationToken).ConfigureAwait(false);
        var movies = await client.GetAsync<List<RadarrMovie>>("/api/v3/movie", cancellationToken).ConfigureAwait(false);
        var profiles = await client.GetAsync<List<ArrQualityProfile>>("/api/v3/qualityprofile", cancellationToken).ConfigureAwait(false);
        var excluded = settings.AddFriendsItems
            ? await ExclusionsAsync(client, "/api/v3/exclusions", e => e.TmdbId, cancellationToken).ConfigureAwait(false)
            : [];

        var plan = ArrPlanner.PlanRadarr(
            movies.Select(m => new ArrMovie(m.Id, m.TmdbId, m.ImdbId, m.Monitored, m.HasFile, m.QualityProfileId)).ToList(),
            friends,
            profiles.ToDictionary(p => p.Id, p => p.Threshold()),
            settings,
            book,
            excluded,
            DateTime.UtcNow,
            Grace);
        if (plan.IsEmpty)
        {
            return version;
        }

        var tag = await TagAsync(client, cancellationToken).ConfigureAwait(false);
        if (plan.Unmonitor.Count > 0)
        {
            await client.SendAsync(HttpMethod.Put, "/api/v3/movie/editor", new { movieIds = plan.Unmonitor.Keys, monitored = false, tags = new[] { tag }, applyTags = "add" }, cancellationToken).ConfigureAwait(false);
            foreach (var (id, tmdb) in plan.Unmonitor)
            {
                book.Unmonitored[id] = tmdb;
                book.GoneSince.Remove(id);
            }

            _logger.LogInformation("Bloom: {Count} movies unmonitored in Radarr: a friend has them", plan.Unmonitor.Count);
        }

        if (plan.Remonitor.Count > 0)
        {
            await client.SendAsync(HttpMethod.Put, "/api/v3/movie/editor", new { movieIds = plan.Remonitor, monitored = true, tags = new[] { tag }, applyTags = "remove" }, cancellationToken).ConfigureAwait(false);
            foreach (var id in plan.Remonitor)
            {
                book.Unmonitored.Remove(id);
                book.GoneSince.Remove(id);
            }

            _logger.LogInformation("Bloom: {Count} movies monitored again in Radarr: no friend has them any more", plan.Remonitor.Count);
            await TryAsync(client.SendAsync(HttpMethod.Post, "/api/v3/command", new { name = "MoviesSearch", movieIds = plan.Remonitor }, cancellationToken), "search").ConfigureAwait(false);
        }

        if (plan.Untag.Count > 0)
        {
            await TryAsync(client.SendAsync(HttpMethod.Put, "/api/v3/movie/editor", new { movieIds = plan.Untag, tags = new[] { tag }, applyTags = "remove" }, cancellationToken), "untag").ConfigureAwait(false);
        }

        foreach (var id in plan.Delete)
        {
            try
            {
                await client.SendAsync(HttpMethod.Delete, $"/api/v3/movie/{id.ToString(CultureInfo.InvariantCulture)}?deleteFiles=false&addImportExclusion=false", null, cancellationToken).ConfigureAwait(false);
            }
            catch (ArrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }

            book.Added.Remove(id);
            book.GoneSince.Remove(id);
        }

        if (plan.Add.Count > 0)
        {
            var (root, profile) = await TargetAsync(client, movies.Select(m => (m.RootFolderPath, m.QualityProfileId)), profiles, cancellationToken).ConfigureAwait(false);
            var added = 0;
            foreach (var friend in plan.Add)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var tmdb = friend.TmdbId ?? (await client.GetAsync<RadarrMovie>("/api/v3/movie/lookup/imdb?imdbId=" + Uri.EscapeDataString(friend.ImdbId!), cancellationToken).ConfigureAwait(false)).TmdbId;
                    if (tmdb <= 0 || book.LeftAlone.Contains(tmdb) || movies.Any(m => m.TmdbId == tmdb))
                    {
                        continue;
                    }

                    var created = await client.SendAsync<RadarrMovie>(
                        HttpMethod.Post,
                        "/api/v3/movie",
                        new
                        {
                            tmdbId = tmdb,
                            title = friend.Title,
                            year = friend.Year,
                            qualityProfileId = profile,
                            rootFolderPath = root,
                            monitored = false,
                            minimumAvailability = "released",
                            tags = new[] { tag },
                            addOptions = new { searchForMovie = false, monitor = "none" }
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (created is { Id: > 0 })
                    {
                        book.Added[created.Id] = created.TmdbId;
                        movies.Add(created);
                        if (++added % SaveEvery == 0)
                        {
                            _store.SaveArrBook(arrBook);
                        }
                    }
                }
                catch (ArrException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                {
                    // Unknown to Radarr's metadata, or added meanwhile: the others still go in.
                    _logger.LogDebug("Bloom: Radarr did not add {Title}: {Message}", friend.Title, ex.Message);
                }
            }

            _logger.LogInformation("Bloom: {Count} friends' movies added to Radarr", added);
        }

        return version;
    }

    private async Task<string?> SonarrAsync(ArrClient client, ArrSettings settings, List<FriendSeries> friends, ArrBook arrBook, CancellationToken cancellationToken)
    {
        var book = arrBook.Sonarr;
        var version = await CheckAppAsync(client, cancellationToken).ConfigureAwait(false);
        var series = await client.GetAsync<List<SonarrSeries>>("/api/v3/series", cancellationToken).ConfigureAwait(false);
        var profiles = await client.GetAsync<List<ArrQualityProfile>>("/api/v3/qualityprofile", cancellationToken).ConfigureAwait(false);
        var excluded = settings.AddFriendsItems
            ? await ExclusionsAsync(client, "/api/v3/importlistexclusion", e => e.TvdbId, cancellationToken).ConfigureAwait(false)
            : [];

        var known = series.Select(s => new ArrSeries(s.Id, s.TvdbId, s.TmdbId is > 0 ? s.TmdbId : null, s.ImdbId, s.Monitored, s.Statistics?.EpisodeFileCount ?? 0, s.QualityProfileId)).ToList();
        var episodes = new Dictionary<int, IReadOnlyList<ArrEpisode>>();
        foreach (var id in ArrPlanner.SeriesToInspect(known, friends, book))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = await client.GetAsync<List<SonarrEpisode>>("/api/v3/episode?seriesId=" + id.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            episodes[id] = list.Select(e => new ArrEpisode(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Monitored, e.HasFile)).ToList();
        }

        var plan = ArrPlanner.PlanSonarr(known, episodes, friends, profiles.ToDictionary(p => p.Id, p => p.Threshold()), settings, book, excluded, DateTime.UtcNow, Grace);
        if (plan.IsEmpty)
        {
            return version;
        }

        var tag = await TagAsync(client, cancellationToken).ConfigureAwait(false);
        if (plan.Unmonitor.Count > 0)
        {
            await client.SendAsync(HttpMethod.Put, "/api/v3/episode/monitor", new { episodeIds = plan.Unmonitor.Keys, monitored = false }, cancellationToken).ConfigureAwait(false);
            foreach (var (id, reference) in plan.Unmonitor)
            {
                book.Unmonitored[id] = reference;
                book.EpisodesGoneSince.Remove(id);
            }

            _logger.LogInformation("Bloom: {Count} episodes unmonitored in Sonarr: a friend has them", plan.Unmonitor.Count);
        }

        if (plan.Remonitor.Count > 0)
        {
            await client.SendAsync(HttpMethod.Put, "/api/v3/episode/monitor", new { episodeIds = plan.Remonitor, monitored = true }, cancellationToken).ConfigureAwait(false);
            foreach (var id in plan.Remonitor)
            {
                book.Unmonitored.Remove(id);
                book.EpisodesGoneSince.Remove(id);
            }

            _logger.LogInformation("Bloom: {Count} episodes monitored again in Sonarr: no friend has them any more", plan.Remonitor.Count);
            await TryAsync(client.SendAsync(HttpMethod.Post, "/api/v3/command", new { name = "EpisodeSearch", episodeIds = plan.Remonitor }, cancellationToken), "search").ConfigureAwait(false);
        }

        foreach (var id in plan.Delete)
        {
            try
            {
                await client.SendAsync(HttpMethod.Delete, $"/api/v3/series/{id.ToString(CultureInfo.InvariantCulture)}?deleteFiles=false&addImportListExclusion=false", null, cancellationToken).ConfigureAwait(false);
            }
            catch (ArrException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }

            book.Added.Remove(id);
            book.SeriesGoneSince.Remove(id);
            book.Tagged.Remove(id);
        }

        if (plan.Tag.Count > 0)
        {
            await client.SendAsync(HttpMethod.Put, "/api/v3/series/editor", new { seriesIds = plan.Tag, tags = new[] { tag }, applyTags = "add" }, cancellationToken).ConfigureAwait(false);
            book.Tagged.UnionWith(plan.Tag);
        }

        if (plan.Untag.Count > 0)
        {
            await client.SendAsync(HttpMethod.Put, "/api/v3/series/editor", new { seriesIds = plan.Untag, tags = new[] { tag }, applyTags = "remove" }, cancellationToken).ConfigureAwait(false);
            book.Tagged.ExceptWith(plan.Untag);
        }

        if (plan.Add.Count > 0)
        {
            var (root, profile) = await TargetAsync(client, series.Select(s => (s.RootFolderPath, s.QualityProfileId)), profiles, cancellationToken).ConfigureAwait(false);
            var added = 0;
            foreach (var friend in plan.Add)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var tvdb = friend.TvdbId ?? await LookupTvdbAsync(client, friend, cancellationToken).ConfigureAwait(false);
                    if (tvdb is not > 0 || book.LeftAloneSeries.Contains(tvdb.Value) || series.Any(s => s.TvdbId == tvdb))
                    {
                        continue;
                    }

                    var created = await client.SendAsync<SonarrSeries>(
                        HttpMethod.Post,
                        "/api/v3/series",
                        new
                        {
                            tvdbId = tvdb,
                            title = friend.Title,
                            qualityProfileId = profile,
                            rootFolderPath = root,
                            monitored = false,
                            seasonFolder = true,
                            tags = new[] { tag },
                            addOptions = new { monitor = "none", searchForMissingEpisodes = false, searchForCutoffUnmetEpisodes = false }
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (created is { Id: > 0 })
                    {
                        book.Added[created.Id] = created.TvdbId;
                        book.Tagged.Add(created.Id);
                        series.Add(created);
                        if (++added % SaveEvery == 0)
                        {
                            _store.SaveArrBook(arrBook);
                        }
                    }
                }
                catch (ArrException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                {
                    _logger.LogDebug("Bloom: Sonarr did not add {Title}: {Message}", friend.Title, ex.Message);
                }
            }

            _logger.LogInformation("Bloom: {Count} friends' series added to Sonarr", added);
        }

        return version;
    }

    private static async Task<int?> LookupTvdbAsync(ArrClient client, FriendSeries friend, CancellationToken cancellationToken)
    {
        var term = friend.TmdbId is int tmdb ? "tmdb:" + tmdb.ToString(CultureInfo.InvariantCulture) : "imdb:" + friend.ImdbId;
        var found = await client.GetAsync<List<SonarrSeries>>("/api/v3/series/lookup?term=" + Uri.EscapeDataString(term), cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault()?.TvdbId;
    }

    private async Task TryAsync(Task call, string what)
    {
        try
        {
            await call.ConfigureAwait(false);
        }
        catch (ArrException ex)
        {
            // Not worth failing the pass: the main change went through.
            _logger.LogWarning("Bloom: {What} failed: {Message}", what, ex.Message);
        }
    }

    /// <summary>
    /// What friends share with us, read from the libraries: their movies, and their series with the episodes they have.
    /// Several friends having the same title count once, with the best copy.
    /// </summary>
    private (List<FriendMovie> Movies, List<FriendSeries> Series) ReadFriendsItems()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            IncludeAlternateVersions = true,
            DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false }
        }).Where(i => _catalog.IsFederatedPath(i.Path)).DistinctBy(i => i.Id).ToList();

        var movies = new Dictionary<string, FriendMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in items.OfType<Movie>())
        {
            var tmdb = IntId(movie, "Tmdb");
            var imdb = StringId(movie, "Imdb");
            var key = tmdb is int t ? "tmdb:" + t.ToString(CultureInfo.InvariantCulture) : imdb is null ? null : "imdb:" + imdb;
            if (key is null)
            {
                continue;
            }

            var friend = new FriendMovie(tmdb, imdb, movie.Name ?? string.Empty, movie.ProductionYear, ArrPlanner.Resolution(movie.Width, movie.Height));
            if (!movies.TryGetValue(key, out var known) || known.Resolution < friend.Resolution)
            {
                movies[key] = friend;
            }
        }

        var series = new Dictionary<string, FriendSeries>(StringComparer.OrdinalIgnoreCase);
        var bySeriesItem = new Dictionary<Guid, FriendSeries>();
        foreach (var show in items.OfType<Series>())
        {
            var tvdb = IntId(show, "Tvdb");
            var tmdb = IntId(show, "Tmdb");
            var imdb = StringId(show, "Imdb");
            var key = tvdb is int v ? "tvdb:" + v.ToString(CultureInfo.InvariantCulture)
                : tmdb is int m ? "tmdb:" + m.ToString(CultureInfo.InvariantCulture)
                : imdb is null ? null : "imdb:" + imdb;
            if (key is null)
            {
                continue;
            }

            if (!series.TryGetValue(key, out var friend))
            {
                friend = new FriendSeries { TvdbId = tvdb, TmdbId = tmdb, ImdbId = imdb, Title = show.Name ?? string.Empty };
                series[key] = friend;
            }

            bySeriesItem[show.Id] = friend;
        }

        foreach (var episode in items.OfType<Episode>())
        {
            if (episode.ParentIndexNumber is not int season || episode.IndexNumber is not int first || !bySeriesItem.TryGetValue(episode.SeriesId, out var friend))
            {
                continue;
            }

            var resolution = ArrPlanner.Resolution(episode.Width, episode.Height);
            for (var number = first; number <= Math.Max(first, episode.IndexNumberEnd ?? first); number++)
            {
                if (!friend.Episodes.TryGetValue((season, number), out var known) || known < resolution)
                {
                    friend.Episodes[(season, number)] = resolution;
                }
            }
        }

        return (movies.Values.ToList(), series.Values.Where(s => s.Episodes.Count > 0).ToList());
    }

    private static string? StringId(BaseItem item, string provider)
    {
        foreach (var (key, value) in item.ProviderIds)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static int? IntId(BaseItem item, string provider)
        => int.TryParse(StringId(item, provider), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
