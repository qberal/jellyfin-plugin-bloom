using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Plays a friend's intro before each of their movies and episodes, through Jellyfin's own intros (the "cinema mode"
/// of its apps, which skips them when resuming). The video is the friend's, downloaded at sync like posters.
/// </summary>
public sealed class FriendIntroProvider : IIntroProvider
{
    private readonly FederationStore _store;
    private readonly FederationPaths _paths;
    private readonly CatalogService _catalog;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FriendIntroProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendIntroProvider"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="paths">Paths.</param>
    /// <param name="catalog">Catalog service.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public FriendIntroProvider(FederationStore store, FederationPaths paths, CatalogService catalog, ILibraryManager libraryManager, ILogger<FriendIntroProvider> logger)
    {
        _store = store;
        _paths = paths;
        _catalog = catalog;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Bloom";

    /// <inheritdoc />
    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        IEnumerable<IntroInfo> none = [];
        if (!_store.Snapshot().Settings.PlayFriendIntros)
        {
            return Task.FromResult(none);
        }

        var peer = _store.FindPeer(_catalog.PeerIdOf(item.Path));
        if (peer is not { Enabled: true, RevokedByPeer: false } || string.IsNullOrEmpty(peer.IntroFile))
        {
            return Task.FromResult(none);
        }

        var path = Path.Combine(_paths.FriendIntroDir, peer.IntroFile);
        if (!File.Exists(path))
        {
            return Task.FromResult(none);
        }

        IEnumerable<IntroInfo> intro = [new IntroInfo { ItemId = FriendIntros.EnsureItem(_libraryManager, path, Text.Get("IntroName", peer.ShownName()), _logger) }];
        return Task.FromResult(intro);
    }
}

/// <summary>
/// Friends' intro videos as Jellyfin items: Jellyfin only plays intros it knows, so each one is registered as a
/// standalone video (outside any library, so it shows up nowhere else).
/// </summary>
public static class FriendIntros
{
    /// <summary>Largest intro accepted, ours or a friend's.</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Returns the item of an intro file, creating it the first time.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="path">Intro file.</param>
    /// <param name="name">Name shown while it plays.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Item id.</returns>
    public static Guid EnsureItem(ILibraryManager libraryManager, string path, string name, ILogger logger)
    {
        var id = libraryManager.GetNewItemId(path, typeof(Video));
        if (libraryManager.GetItemById(id) is null)
        {
            var now = DateTime.UtcNow;
            var video = new Video
            {
                Id = id,
                Path = path,
                Name = name,
                DateCreated = now,
                DateModified = File.GetLastWriteTimeUtc(path),
            };
            libraryManager.CreateItem(video, null);
            logger.LogInformation("Bloom: registered intro {Path}", path);
        }

        return id;
    }

    /// <summary>
    /// Removes an intro: its item and its file.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="path">Intro file.</param>
    public static void Forget(ILibraryManager libraryManager, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (libraryManager.GetItemById(libraryManager.GetNewItemId(path, typeof(Video))) is { } item)
        {
            libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// File extension for an intro, from its media type (video files only).
    /// </summary>
    /// <param name="mediaType">Media type, such as "video/mp4".</param>
    /// <returns>The extension (".mp4"), or null when it is not a video Bloom accepts.</returns>
    public static string? Extension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "video/mp4" => ".mp4",
        "video/x-m4v" => ".m4v",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        "video/x-matroska" or "video/matroska" => ".mkv",
        _ => null
    };

    /// <summary>
    /// Media type of an intro file, from its extension.
    /// </summary>
    /// <param name="path">File.</param>
    /// <returns>The media type.</returns>
    public static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m4v" => "video/x-m4v",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        _ => "video/mp4"
    };
}
