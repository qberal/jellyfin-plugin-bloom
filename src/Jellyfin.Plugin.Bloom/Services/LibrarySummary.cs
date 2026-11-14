using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Bloom.Services;

#pragma warning disable SA1402 // Small records grouped on purpose.

/// <summary>A movie, series or episode, as the summary sees it.</summary>
/// <param name="Id">Item id.</param>
/// <param name="PeerId">The friend it comes from (null: ours).</param>
/// <param name="PrimaryVersionId">The item it is a version of, if any.</param>
/// <param name="Keys">Provider keys ("tvdb:81189"...), for series.</param>
public sealed record SummaryItem(Guid Id, string? PeerId, Guid? PrimaryVersionId, IReadOnlyList<string> Keys);

/// <summary>How many items of a kind are ours, and how many come from friends.</summary>
public sealed class SummaryCount
{
    /// <summary>Gets or sets how many are ours.</summary>
    public int Own { get; set; }

    /// <summary>Gets or sets how many copies come from friends (every copy, as Jellyfin's dashboard counts them).</summary>
    public int Friends { get; set; }

    /// <summary>Gets or sets how many titles only friends have (each counted once, however many copies).</summary>
    public int New { get; set; }

    /// <summary>Gets or sets how many copies come from each friend.</summary>
    public Dictionary<string, int> ByFriend { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets the total, as Jellyfin's dashboard shows it: every copy, ours and friends'.</summary>
    public int Total => Own + Friends;
}

/// <summary>What is ours and what comes from friends, by kind.</summary>
public sealed class LibrarySummaryCounts
{
    /// <summary>Gets or sets the movies.</summary>
    public SummaryCount Movies { get; set; } = new();

    /// <summary>Gets or sets the series.</summary>
    public SummaryCount Series { get; set; } = new();

    /// <summary>Gets or sets the episodes.</summary>
    public SummaryCount Episodes { get; set; } = new();
}

/// <summary>
/// Counts what is in the libraries: ours, and what comes from friends. Jellyfin's dashboard shows the sum of every
/// copy without telling them apart, and counts a friend's other version of one of our movies as one more movie.
/// </summary>
public sealed class LibrarySummary
{
    private readonly ILibraryManager _libraryManager;
    private readonly CatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySummary"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="catalog">Catalog service.</param>
    public LibrarySummary(ILibraryManager libraryManager, CatalogService catalog)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
    }

    /// <summary>
    /// Counts the movies or the episodes. A friend's copy that is a version of one of ours is not new, nor is one that
    /// is a version of another friend's copy of a title we don't have (that title counts once).
    /// </summary>
    /// <param name="videos">Every movie (or every episode), versions included.</param>
    /// <returns>The counts.</returns>
    public static SummaryCount CountVideos(IReadOnlyList<SummaryItem> videos)
    {
        var count = new SummaryCount();
        var byId = videos.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var video in byId.Values)
        {
            if (video.PeerId is null)
            {
                count.Own++;
                continue;
            }

            count.Friends++;
            count.ByFriend[video.PeerId] = count.ByFriend.GetValueOrDefault(video.PeerId) + 1;

            // The copy the others are versions of stands for the title.
            if (video.PrimaryVersionId is not Guid id || !byId.ContainsKey(id))
            {
                count.New++;
            }
        }

        return count;
    }

    /// <summary>
    /// Counts the series. A friend's series we also have (they bring episodes we miss) is not new; a series only
    /// friends have counts once, however many of them have it.
    /// </summary>
    /// <param name="series">Every series.</param>
    /// <returns>The counts.</returns>
    public static SummaryCount CountSeries(IReadOnlyList<SummaryItem> series)
    {
        var count = new SummaryCount();
        var ownKeys = new HashSet<string>(series.Where(s => s.PeerId is null).SelectMany(s => s.Keys), StringComparer.OrdinalIgnoreCase);
        var groups = new List<HashSet<string>>();
        foreach (var show in series.DistinctBy(s => s.Id))
        {
            if (show.PeerId is null)
            {
                count.Own++;
                continue;
            }

            count.Friends++;
            count.ByFriend[show.PeerId] = count.ByFriend.GetValueOrDefault(show.PeerId) + 1;
            if (show.Keys.Any(ownKeys.Contains))
            {
                continue;
            }

            // Two friends with the same show: one new series.
            var group = show.Keys.Count == 0 ? null : groups.Find(g => show.Keys.Any(g.Contains));
            if (group is null)
            {
                groups.Add(new HashSet<string>(show.Keys, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                group.UnionWith(show.Keys);
            }
        }

        count.New = groups.Count;
        return count;
    }

    /// <summary>
    /// Counts what is in the libraries.
    /// </summary>
    /// <returns>The counts.</returns>
    public LibrarySummaryCounts Count()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            IncludeAlternateVersions = true,
            DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false }
        }).DistinctBy(i => i.Id).ToList();

        SummaryItem Of(BaseItem item) => new(
            item.Id,
            _catalog.PeerIdOf(item.Path),
            item is Video video ? video.PrimaryVersionId : null,
            item is Series ? OwnedIndex.Keys(item.ProviderIds).ToList() : []);

        return new LibrarySummaryCounts
        {
            Movies = CountVideos(items.OfType<Movie>().Select(Of).ToList()),
            Series = CountSeries(items.OfType<Series>().Select(Of).ToList()),
            Episodes = CountVideos(items.OfType<Episode>().Select(Of).ToList())
        };
    }
}
