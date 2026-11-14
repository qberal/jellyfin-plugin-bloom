using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Api;

/// <summary>
/// Administration API used by the dashboard page. Requires a Jellyfin administrator.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bloom/Admin")]
[UsePageLanguage]
public class AdminController : ControllerBase
{
    private const int MaxAliasLength = 40;

    private readonly FederationStore _store;
    private readonly ServerIdentity _identity;
    private readonly PairingService _pairing;
    private readonly CatalogService _catalog;
    private readonly VersionLinker _versions;
    private readonly SyncService _sync;
    private readonly LibraryIntegration _libraries;
    private readonly PeerClient _client;
    private readonly FederationPaths _paths;
    private readonly ArrSync _arr;
    private readonly LibrarySummary _summary;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<AdminController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="identity">Identity.</param>
    /// <param name="pairing">Pairing.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="sync">Sync.</param>
    /// <param name="libraries">Library integration.</param>
    /// <param name="client">Peer client.</param>
    /// <param name="paths">Paths.</param>
    /// <param name="versions">Version linker.</param>
    /// <param name="arr">Radarr and Sonarr.</param>
    /// <param name="summary">What is ours and what comes from friends.</param>
    /// <param name="appHost">Application host.</param>
    /// <param name="logger">Logger.</param>
    public AdminController(
        FederationStore store,
        ServerIdentity identity,
        PairingService pairing,
        CatalogService catalog,
        SyncService sync,
        LibraryIntegration libraries,
        PeerClient client,
        VersionLinker versions,
        FederationPaths paths,
        ArrSync arr,
        LibrarySummary summary,
        IServerApplicationHost appHost,
        ILogger<AdminController> logger)
    {
        _arr = arr;
        _summary = summary;
        _versions = versions;
        _paths = paths;
        _store = store;
        _identity = identity;
        _pairing = pairing;
        _catalog = catalog;
        _sync = sync;
        _libraries = libraries;
        _client = client;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Everything the page needs.
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    public ActionResult<object> Status()
    {
        var state = _store.Snapshot();
        string? suggested = null;
        try
        {
            suggested = _appHost.GetSmartApiUrl(Request);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Bloom: no smart URL");
        }

        return new
        {
            Identity = new
            {
                _identity.ServerId,
                _identity.Fingerprint,
                Name = _store.GetDisplayName(),
                SuggestedUrl = suggested,
                Version = typeof(Plugin).Assembly.GetName().Version?.ToString()
            },
            state.Settings,
            Arr = ArrStatus(state.Arr),
            Intro = OwnIntro() is { } intro ? new { Size = new FileInfo(intro).Length, state.Settings.IntroTag } : null,
            MediaRoot = _store.GetMediaRoot(),
            ShareableLibraries = _catalog.GetLocalShareableLibraries(),
            MergeTargets = _libraries.ListLibraries()
                .Where(l => l.Locations.Length == 0 || l.Locations.Any(x => !_catalog.IsFederatedPath(x)))
                .Select(l => new { l.Name, l.CollectionType }),
            Invites = state.Invites.Where(i => i.ExpiresUtc > DateTime.UtcNow).Select(i => new { i.Label, i.ExpiresUtc, i.SharedLibraryIds }),
            Peers = state.Peers.OrderBy(p => p.ShownName(), StringComparer.CurrentCultureIgnoreCase).Select(p => new
            {
                p.Id,
                p.Name,
                p.Url,
                Fingerprint = ServerIdentity.ComputeFingerprint(p.PublicKey),
                p.Enabled,
                p.RevokedByPeer,
                p.DateAdded,
                p.LastSeen,
                p.LastSync,
                p.LastError,
                p.Sleeps,
                p.Alias,
                ShownName = p.ShownName(),
                HasIntro = p.IntroFile is not null,

                // The tag their items carry, to filter them in Jellyfin (written in the server's language).
                Tag = Text.GetInServerLanguage("FriendTag", p.ShownName()),
                Busy = _sync.IsBusy(p.Id),
                p.SharedLibraryIds,
                p.Imports
            })
        };
    }

    /// <summary>
    /// What is in the libraries: ours, and what comes from each friend (Jellyfin's dashboard shows only the sum).
    /// </summary>
    /// <returns>The counts.</returns>
    [HttpGet("Library")]
    public ActionResult<LibrarySummaryCounts> Library() => _summary.Count();

    /// <summary>
    /// Saves the settings.
    /// </summary>
    /// <param name="request">Settings.</param>
    /// <returns>Nothing.</returns>
    [HttpPost("Settings")]
    public async Task<ActionResult> SaveSettings([FromBody] FederationSettings request)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(request.PublicUrl))
        {
            url = PairingService.NormalizeUrl(request.PublicUrl);
            if (url is null)
            {
                return BadRequest(new { error = Text.Get("InvalidPublicUrl") });
            }
        }

        string? mediaRoot = null;
        if (!string.IsNullOrWhiteSpace(request.MediaRoot))
        {
            if (!Path.IsPathFullyQualified(request.MediaRoot))
            {
                return BadRequest(new { error = Text.Get("MediaRootNotAbsolute") });
            }

            mediaRoot = Path.GetFullPath(request.MediaRoot);
        }

        var previousRoot = _store.GetMediaRoot();
        var previous = _store.Snapshot().Settings;
        _store.Update(s =>
        {
            s.Settings.PublicUrl = url;
            s.Settings.ServerName = string.IsNullOrWhiteSpace(request.ServerName) ? null : request.ServerName.Trim();
            s.Settings.SkipOwnedItems = request.SkipOwnedItems;
            s.Settings.NotifyPeersOnChange = request.NotifyPeersOnChange;
            s.Settings.BadgePosters = request.BadgePosters;
            s.Settings.BadgeStyle = PosterBadge.ParseStyle(request.BadgeStyle).ToString();
            s.Settings.GroupVersions = request.GroupVersions;
            s.Settings.SleepsWhenIdle = request.SleepsWhenIdle;
            s.Settings.PlayFriendIntros = request.PlayFriendIntros;
        });

        // These change what friends' libraries look like here: apply them now rather than in 6 hours.
        if (previous.GroupVersions != request.GroupVersions)
        {
            // Groups between friends' copies do not depend on what is imported: regroup right away.
            _ = Task.Run(() => _versions.LinkAsync(CancellationToken.None));
        }

        // Friends that are asleep get the change when they wake up.
        var now = DateTime.UtcNow;
        var awake = _store.Snapshot().Peers.Where(p => p.Enabled && !p.RevokedByPeer && !p.IsAsleep(now)).ToList();
        var styleChanged = !string.Equals(previous.BadgeStyle, PosterBadge.ParseStyle(request.BadgeStyle).ToString(), StringComparison.Ordinal);
        if (previous.SkipOwnedItems != request.SkipOwnedItems || previous.BadgePosters != request.BadgePosters || previous.GroupVersions != request.GroupVersions || styleChanged)
        {
            foreach (var peer in awake)
            {
                _sync.QueueSync(peer.Id, TimeSpan.FromSeconds(2));
            }
        }

        if (previous.SleepsWhenIdle != request.SleepsWhenIdle)
        {
            // Tell friends now whether they may contact us on their own.
            foreach (var peer in awake)
            {
                NotifyInBackground(peer, "settings");
            }
        }

        if (!string.Equals(previousRoot, _store.MediaRootFor(mediaRoot), StringComparison.Ordinal))
        {
            _logger.LogInformation("Bloom: moving friends' items from {Old} to {New}", previousRoot, _store.MediaRootFor(mediaRoot));
            await _sync.MoveMediaRootAsync(() => _store.Update(s => s.Settings.MediaRoot = mediaRoot)).ConfigureAwait(false);
        }
        else
        {
            _store.Update(s => s.Settings.MediaRoot = mediaRoot);
        }

        return NoContent();
    }

    /// <summary>
    /// Saves how Bloom works with Radarr and Sonarr, then checks them in the background (the page polls the status).
    /// </summary>
    /// <param name="request">Settings. An empty API key keeps the saved one; an empty address disconnects.</param>
    /// <returns>Nothing.</returns>
    [HttpPost("Arr")]
    public ActionResult SaveArr([FromBody] ArrRequest request)
    {
        var current = _store.Snapshot().Arr;
        var book = _store.LoadArrBook();

        // Disconnecting one where Bloom changed things keeps it until they are undone (see ArrSync).
        static ArrConnection Released(ArrConnection saved, bool changed)
            => saved.IsSet && changed ? new ArrConnection { Url = saved.Url, ApiKey = saved.ApiKey, Release = true } : new ArrConnection();

        var radarr = Connection("Radarr", request.RadarrUrl, request.RadarrApiKey, current.Radarr, out var error)
            ?? (error is null ? Released(current.Radarr, book.Radarr.Unmonitored.Count + book.Radarr.Added.Count > 0) : null);
        if (radarr is null)
        {
            return BadRequest(new { error });
        }

        var sonarr = Connection("Sonarr", request.SonarrUrl, request.SonarrApiKey, current.Sonarr, out error)
            ?? (error is null ? Released(current.Sonarr, book.Sonarr.Unmonitored.Count + book.Sonarr.Added.Count + book.Sonarr.Tagged.Count > 0) : null);
        if (sonarr is null)
        {
            return BadRequest(new { error });
        }

        _store.Update(s => s.Arr = new ArrSettings
        {
            Radarr = radarr,
            Sonarr = sonarr,
            Avoid = Enum.IsDefined(request.Avoid) ? request.Avoid : ArrAvoidMode.GoodEnough,
            AddFriendsItems = request.AddFriendsItems
        });
        _arr.Queue();
        return NoContent();
    }

    /// <summary>
    /// Creates an invite code to send to a friend.
    /// </summary>
    /// <param name="request">Invite options.</param>
    /// <returns>The code.</returns>
    [HttpPost("Invites")]
    public ActionResult<object> CreateInvite([FromBody] InviteRequest request)
    {
        try
        {
            var (code, expires) = _pairing.CreateInvite(request.SharedLibraryIds ?? [], request.Label);
            return new { Code = code, ExpiresUtc = expires };
        }
        catch (FederationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes all pending invites.
    /// </summary>
    /// <returns>Nothing.</returns>
    [HttpDelete("Invites")]
    public ActionResult DeleteInvites()
    {
        _store.Update(s => s.Invites.Clear());
        return NoContent();
    }

    /// <summary>
    /// Decodes an invite to show who it comes from before accepting.
    /// </summary>
    /// <param name="request">Code.</param>
    /// <returns>Inviter details.</returns>
    [HttpPost("Invites/Inspect")]
    public ActionResult<object> InspectInvite([FromBody] AcceptRequest request)
    {
        try
        {
            var invite = PairingService.DecodeInvite(request.Code);
            return new
            {
                invite.Name,
                invite.Url,
                Fingerprint = ServerIdentity.ComputeFingerprint(invite.Key),
                ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(invite.Exp).UtcDateTime,
                AlreadyFriend = _store.FindPeer(invite.Id) is not null,
                IsSelf = invite.Id == _identity.ServerId
            };
        }
        catch (FederationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Accepts a friend's invite (key exchange) and starts a first sync.
    /// </summary>
    /// <param name="request">Code and libraries shared back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The friend id.</returns>
    [HttpPost("Invites/Accept")]
    public async Task<ActionResult<object>> AcceptInvite([FromBody] AcceptRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var peer = await _pairing.AcceptInviteAsync(request.Code ?? string.Empty, request.SharedLibraryIds ?? [], cancellationToken).ConfigureAwait(false);
            _sync.QueueSync(peer.Id, TimeSpan.Zero);
            NotifyInBackground(peer, "paired");
            return new { peer.Id, peer.Name };
        }
        catch (FederationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PeerRequestException ex)
        {
            return BadRequest(new { error = Text.Get("FriendRefused", ex.Message) });
        }
    }

    /// <summary>
    /// Updates what we share with a friend and how we import its libraries.
    /// </summary>
    /// <param name="peerId">Friend id.</param>
    /// <param name="request">Changes.</param>
    /// <returns>Nothing.</returns>
    [HttpPost("Peers/{peerId}")]
    public async Task<ActionResult> UpdatePeer([FromRoute] string peerId, [FromBody] PeerUpdateRequest request)
    {
        var mergeTargets = _libraries.ListLibraries();
        string? error = null;
        var sharesChanged = false;
        var renamed = false;
        var found = _store.UpdatePeer(peerId, p =>
        {
            if (request.Enabled.HasValue)
            {
                p.Enabled = request.Enabled.Value;
            }

            if (request.Alias is not null)
            {
                var alias = string.IsNullOrWhiteSpace(request.Alias) ? null : request.Alias.Trim();
                alias = alias is { Length: > MaxAliasLength } ? alias[..MaxAliasLength].TrimEnd() : alias;
                if (!string.Equals(alias, p.Alias, StringComparison.Ordinal))
                {
                    p.Alias = alias;
                    renamed = true;
                }
            }

            if (request.SharedLibraryIds is not null)
            {
                var updated = request.SharedLibraryIds.Distinct().ToList();
                sharesChanged = !updated.ToHashSet().SetEquals(p.SharedLibraryIds);
                p.SharedLibraryIds = updated;
            }

            foreach (var change in request.Imports ?? [])
            {
                var import = p.Imports.Find(i => i.RemoteLibraryId == change.RemoteLibraryId);
                if (import is null)
                {
                    continue;
                }

                if (change.Mode == ImportMode.Merge)
                {
                    var target = mergeTargets.FirstOrDefault(l => l.Name == change.TargetLibrary);
                    if (target.Name is null || !string.Equals(target.CollectionType, import.CollectionType, StringComparison.OrdinalIgnoreCase))
                    {
                        error = Text.Get("WrongLibraryType", change.TargetLibrary, Text.Get(import.CollectionType == "tvshows" ? "ShowsType" : "MoviesType"));
                        continue;
                    }

                    import.TargetLibrary = target.Name;
                }

                import.Mode = change.Mode;
            }
        });

        if (!found)
        {
            return NotFound(new { error = Text.Get("UnknownFriend") });
        }

        if (renamed)
        {
            // Deliberate: the next sync renames the friend's files after the new name (version picker included).
            await _sync.RenameFilesAtNextSyncAsync(peerId).ConfigureAwait(false);
        }

        _sync.QueueSync(peerId, TimeSpan.FromSeconds(1));
        if (sharesChanged)
        {
            var peer = _store.FindPeer(peerId);
            if (peer is not null)
            {
                _catalog.Invalidate();
                NotifyInBackground(peer, "shares-changed");
            }
        }

        return error is null ? NoContent() : BadRequest(new { error });
    }

    /// <summary>
    /// Syncs a friend now.
    /// </summary>
    /// <param name="peerId">Friend id.</param>
    /// <returns>Result.</returns>
    [HttpPost("Peers/{peerId}/Sync")]
    public async Task<ActionResult<object>> SyncPeer([FromRoute] string peerId)
    {
        if (_store.FindPeer(peerId) is null)
        {
            return NotFound(new { error = Text.Get("UnknownFriend") });
        }

        // Not tied to the HTTP request: closing the page must not abort a sync halfway.
        var error = await _sync.SyncPeerAsync(peerId, CancellationToken.None).ConfigureAwait(false);
        return error is null ? new { ok = true } : BadRequest(new { error });
    }

    /// <summary>
    /// Ends the federation with a friend: tells them, removes their items and forgets their key.
    /// </summary>
    /// <param name="peerId">Friend id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("Peers/{peerId}")]
    public async Task<ActionResult> DeletePeer([FromRoute] string peerId, CancellationToken cancellationToken)
    {
        var peer = _store.FindPeer(peerId);
        if (peer is null)
        {
            return NotFound(new { error = Text.Get("UnknownFriend") });
        }

        if (!peer.RevokedByPeer)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _client.SendJsonAsync<object>(PeerEndpoint.From(peer), HttpMethod.Post, "/Bloom/Peer/Unpair", new { }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PeerRequestException or OperationCanceledException)
            {
                _logger.LogWarning("Bloom: could not tell {Peer} that we left: {Message}", peer.Name, ex.Message);
            }
        }

        await _sync.RemovePeerAsync(peerId).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Sets this server's intro: the video friends play before our movies. The body is the video itself.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Its size.</returns>
    [HttpPost("Intro")]
    [RequestSizeLimit(FriendIntros.MaxBytes + (1024 * 1024))]
    public async Task<ActionResult<object>> UploadIntro(CancellationToken cancellationToken)
    {
        var extension = FriendIntros.Extension(Request.ContentType?.Split(';')[0].Trim());
        if (extension is null)
        {
            return BadRequest(new { error = Text.Get("IntroNotVideo") });
        }

        if (Request.ContentLength > FriendIntros.MaxBytes)
        {
            return BadRequest(new { error = Text.Get("IntroTooLarge") });
        }

        Directory.CreateDirectory(_paths.IntroDir);
        var upload = Path.Combine(_paths.IntroDir, "upload.part");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var output = System.IO.File.Create(upload);
        await using (output.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > FriendIntros.MaxBytes)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        if (total > FriendIntros.MaxBytes || total == 0)
        {
            System.IO.File.Delete(upload);
            return BadRequest(new { error = Text.Get(total == 0 ? "IntroNotVideo" : "IntroTooLarge") });
        }

        var tag = Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
        var name = "intro" + extension;
        var previous = OwnIntro();
        System.IO.File.Move(upload, Path.Combine(_paths.IntroDir, name), true);
        if (previous is not null && !string.Equals(Path.GetFileName(previous), name, StringComparison.Ordinal))
        {
            System.IO.File.Delete(previous);
        }

        _store.Update(s =>
        {
            s.Settings.IntroFile = name;
            s.Settings.IntroTag = tag;
        });
        NotifyFriends("intro");
        return new { Size = total, IntroTag = tag };
    }

    /// <summary>
    /// Removes this server's intro.
    /// </summary>
    /// <returns>Nothing.</returns>
    [HttpDelete("Intro")]
    public ActionResult DeleteIntro()
    {
        if (OwnIntro() is { } path)
        {
            System.IO.File.Delete(path);
        }

        _store.Update(s =>
        {
            s.Settings.IntroFile = null;
            s.Settings.IntroTag = null;
        });
        NotifyFriends("intro");
        return NoContent();
    }

    /// <summary>
    /// This server's intro, for the preview on the settings page.
    /// </summary>
    /// <returns>The video.</returns>
    [HttpGet("Intro")]
    public ActionResult GetIntro()
    {
        var path = OwnIntro();
        return path is null ? NotFound() : PhysicalFile(path, FriendIntros.MediaType(path), enableRangeProcessing: true);
    }

    /// <summary>
    /// Builds a connection from the page's fields: null with no error means "not used".
    /// </summary>
    private static ArrConnection? Connection(string app, string? url, string? apiKey, ArrConnection saved, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalized = ArrClient.NormalizeUrl(url);
        if (normalized is null)
        {
            error = Text.Get("ArrInvalidUrl", app);
            return null;
        }

        // An empty key keeps the saved one, unless it belongs to a disconnected address.
        var key = string.IsNullOrWhiteSpace(apiKey) ? (saved.Release ? null : saved.ApiKey) : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || !key.All(c => c is > ' ' and <= '~'))
        {
            error = Text.Get("ArrMissingKey", app);
            return null;
        }

        return new ArrConnection { Url = normalized, ApiKey = key };
    }

    /// <summary>
    /// What the page shows about Radarr and Sonarr (never the API keys).
    /// </summary>
    private object ArrStatus(ArrSettings settings)
    {
        var book = _store.LoadArrBook();
        return new
        {
            settings.Avoid,
            settings.AddFriendsItems,
            Running = _arr.IsBusy,
            Radarr = new
            {
                Url = settings.Radarr.Release ? null : settings.Radarr.Url,
                HasApiKey = !settings.Radarr.Release && !string.IsNullOrWhiteSpace(settings.Radarr.ApiKey),
                Releasing = settings.Radarr.Release,
                book.Radarr.Status.LastRun,
                book.Radarr.Status.Version,
                book.Radarr.Status.Error,
                NotDownloaded = book.Radarr.Unmonitored.Count,
                Added = book.Radarr.Added.Count
            },
            Sonarr = new
            {
                Url = settings.Sonarr.Release ? null : settings.Sonarr.Url,
                HasApiKey = !settings.Sonarr.Release && !string.IsNullOrWhiteSpace(settings.Sonarr.ApiKey),
                Releasing = settings.Sonarr.Release,
                book.Sonarr.Status.LastRun,
                book.Sonarr.Status.Version,
                book.Sonarr.Status.Error,
                NotDownloaded = book.Sonarr.Unmonitored.Count,
                Added = book.Sonarr.Added.Count
            }
        };
    }

    private string? OwnIntro()
    {
        var file = _store.Snapshot().Settings.IntroFile;
        var path = file is null ? null : Path.Combine(_paths.IntroDir, file);
        return path is not null && System.IO.File.Exists(path) ? path : null;
    }

    // Friends that are awake hear about it now; the others when they wake up (they sync with us then).
    private void NotifyFriends(string reason)
    {
        var now = DateTime.UtcNow;
        foreach (var peer in _store.Snapshot().Peers.Where(p => p.Enabled && !p.RevokedByPeer && !p.IsAsleep(now)))
        {
            NotifyInBackground(peer, reason);
        }
    }

    private void NotifyInBackground(PeerRecord peer, string reason)
    {
        if (peer.IsAsleep(DateTime.UtcNow))
        {
            // Not worth waking it up: it syncs with us when it wakes up.
            return;
        }

        var request = new NotifyRequest { Reason = reason, Sleeps = _store.Snapshot().Settings.SleepsWhenIdle };
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await _client.SendJsonAsync<object>(PeerEndpoint.From(peer), HttpMethod.Post, "/Bloom/Peer/Notify", request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PeerRequestException ex)
            {
                _logger.LogDebug("Bloom: notify {Peer} failed: {Message}", peer.Name, ex.Message);
            }
        });
    }

    /// <summary>Radarr and Sonarr settings.</summary>
    public sealed class ArrRequest
    {
        /// <summary>Gets or sets Radarr's address (empty: not used).</summary>
        public string? RadarrUrl { get; set; }

        /// <summary>Gets or sets Radarr's API key (empty: keep the saved one).</summary>
        public string? RadarrApiKey { get; set; }

        /// <summary>Gets or sets Sonarr's address (empty: not used).</summary>
        public string? SonarrUrl { get; set; }

        /// <summary>Gets or sets Sonarr's API key (empty: keep the saved one).</summary>
        public string? SonarrApiKey { get; set; }

        /// <summary>Gets or sets what they do with what a friend already has.</summary>
        public ArrAvoidMode Avoid { get; set; } = ArrAvoidMode.GoodEnough;

        /// <summary>Gets or sets a value indicating whether friends' movies and series are added to them.</summary>
        public bool AddFriendsItems { get; set; }
    }

    /// <summary>Invite creation request.</summary>
    public sealed class InviteRequest
    {
        /// <summary>Gets or sets the libraries shared with the new friend.</summary>
        public List<Guid>? SharedLibraryIds { get; set; }

        /// <summary>Gets or sets a label.</summary>
        public string? Label { get; set; }
    }

    /// <summary>Invite acceptance request.</summary>
    public sealed class AcceptRequest
    {
        /// <summary>Gets or sets the code.</summary>
        public string? Code { get; set; }

        /// <summary>Gets or sets our libraries shared back.</summary>
        public List<Guid>? SharedLibraryIds { get; set; }
    }

    /// <summary>Friend update request.</summary>
    public sealed class PeerUpdateRequest
    {
        /// <summary>Gets or sets whether the federation is active.</summary>
        public bool? Enabled { get; set; }

        /// <summary>Gets or sets our libraries shared with this friend.</summary>
        public List<Guid>? SharedLibraryIds { get; set; }

        /// <summary>Gets or sets import changes.</summary>
        public List<ImportChange>? Imports { get; set; }

        /// <summary>Gets or sets the name shown for this friend here (empty: the one its server announces; null: unchanged).</summary>
        public string? Alias { get; set; }
    }

    /// <summary>Change of an import.</summary>
    public sealed class ImportChange
    {
        /// <summary>Gets or sets the friend's library id.</summary>
        public string RemoteLibraryId { get; set; } = string.Empty;

        /// <summary>Gets or sets the mode.</summary>
        public ImportMode Mode { get; set; }

        /// <summary>Gets or sets the target library (Merge).</summary>
        public string? TargetLibrary { get; set; }
    }
}
