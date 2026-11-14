using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bloom.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// What we know of the video of a friend's copy, from their catalog: written to its .nfo, Kodi style.
/// </summary>
/// <param name="Codec">Video codec (hevc, h264...), if known.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Range">Dynamic range (SDR, HDR10, DOVIWithHDR10...).</param>
public sealed record VideoDetails(string? Codec, int Width, int Height, VideoRangeType Range)
{
    private const string Pq = "smpte2084";
    private const string Hlg = "arib-std-b67";

    /// <summary>
    /// Reads the video details of a friend's catalog item.
    /// </summary>
    /// <param name="item">Friend's movie or episode.</param>
    /// <returns>The details, or null when the friend did not send the size of the video.</returns>
    public static VideoDetails? From(CatalogItem item)
    {
        if (item.Width is not > 0 || item.Height is not > 0)
        {
            return null;
        }

        var range = Enum.TryParse<VideoRangeType>(item.VideoRange, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : VideoRangeType.SDR;
        return new VideoDetails(CleanCodec(item.VideoCodec), item.Width.Value, item.Height.Value, range);
    }

    /// <summary>
    /// Writes the details as Kodi's <c>fileinfo/streamdetails/video</c>, plus Jellyfin's exact range.
    /// </summary>
    /// <param name="w">Writer, inside the root element of the .nfo.</param>
    public void Write(XmlWriter w)
    {
        w.WriteStartElement("fileinfo");
        w.WriteStartElement("streamdetails");
        w.WriteStartElement("video");
        if (Codec is not null)
        {
            w.WriteElementString("codec", Codec);
        }

        w.WriteElementString("width", Width.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("height", Height.ToString(CultureInfo.InvariantCulture));
        if (KodiHdrType(Range) is string hdr)
        {
            w.WriteElementString("hdrtype", hdr);
        }

        if (Range is not (VideoRangeType.SDR or VideoRangeType.Unknown))
        {
            w.WriteElementString("videorangetype", Range.ToString());
        }

        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>
    /// Reads the details back from an .nfo written by <see cref="Write"/>.
    /// </summary>
    /// <param name="nfo">Content of the .nfo.</param>
    /// <returns>The details, or null when there are none (or the file is not valid).</returns>
    public static VideoDetails? Parse(string nfo)
    {
        XElement? video;
        try
        {
            using var reader = XmlReader.Create(new StringReader(nfo), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            video = XDocument.Load(reader).Root?.Element("fileinfo")?.Element("streamdetails")?.Element("video");
        }
        catch (XmlException)
        {
            return null;
        }

        if (video is null
            || !int.TryParse(video.Element("width")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(video.Element("height")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0 || width > 20000 || height > 20000)
        {
            return null;
        }

        var range = Enum.TryParse<VideoRangeType>(video.Element("videorangetype")?.Value, true, out var exact) && Enum.IsDefined(exact)
            ? exact
            : video.Element("hdrtype")?.Value.Trim().ToLowerInvariant() switch
            {
                "hdr10" => VideoRangeType.HDR10,
                "hlg" => VideoRangeType.HLG,
                "dolbyvision" => VideoRangeType.DOVI,
                _ => VideoRangeType.SDR
            };
        return new VideoDetails(CleanCodec(video.Element("codec")?.Value), width, height, range);
    }

    /// <summary>
    /// Builds the video stream Jellyfin would have found by probing the friend's file.
    /// </summary>
    /// <returns>The stream (index 0).</returns>
    public MediaStream ToStream()
    {
        var stream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = Codec,
            Width = Width,
            Height = Height,
            IsDefault = true
        };

        // Jellyfin derives the range from these fields (MediaStream.GetVideoColorRange): set them so it finds the
        // friend's range again.
        void Bt2020(string transfer)
        {
            stream.ColorTransfer = transfer;
            stream.ColorPrimaries = "bt2020";
            stream.ColorSpace = "bt2020nc";
        }

        void DolbyVision(int profile, int compatibility)
        {
            stream.DvVersionMajor = 1;
            stream.DvProfile = profile;
            stream.DvBlSignalCompatibilityId = compatibility;
            stream.RpuPresentFlag = 1;
            stream.BlPresentFlag = 1;
        }

        switch (Range)
        {
            case VideoRangeType.HDR10:
                Bt2020(Pq);
                break;
            case VideoRangeType.HDR10Plus:
                Bt2020(Pq);
                stream.Hdr10PlusPresentFlag = true;
                break;
            case VideoRangeType.HLG:
                Bt2020(Hlg);
                break;
            case VideoRangeType.DOVI:
                DolbyVision(5, 0);
                break;
            case VideoRangeType.DOVIWithHDR10:
                DolbyVision(8, 1);
                Bt2020(Pq);
                break;
            case VideoRangeType.DOVIWithHDR10Plus:
                DolbyVision(8, 1);
                Bt2020(Pq);
                stream.Hdr10PlusPresentFlag = true;
                break;
            case VideoRangeType.DOVIWithHLG:
                DolbyVision(8, 4);
                Bt2020(Hlg);
                break;
            case VideoRangeType.DOVIWithSDR:
                DolbyVision(8, 2);
                break;
            case VideoRangeType.DOVIWithEL:
                DolbyVision(7, 6);
                Bt2020(Pq);
                break;
            case VideoRangeType.DOVIWithELHDR10Plus:
                DolbyVision(7, 6);
                Bt2020(Pq);
                stream.Hdr10PlusPresentFlag = true;
                break;
            case VideoRangeType.DOVIInvalid:
                DolbyVision(8, 0);
                break;
        }

        return stream;
    }

    private static string? KodiHdrType(VideoRangeType range) => range switch
    {
        VideoRangeType.HDR10 or VideoRangeType.HDR10Plus => "hdr10",
        VideoRangeType.HLG => "hlg",
        VideoRangeType.DOVIInvalid or VideoRangeType.SDR or VideoRangeType.Unknown => null,
        _ => "dolbyvision"
    };

    private static string? CleanCodec(string? codec)
    {
        var clean = new string((codec ?? string.Empty).Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').Take(32).ToArray());
        return clean.Length == 0 ? null : clean;
    }
}

/// <summary>
/// Jellyfin only learns what a .strm holds (size, codec, HDR) the first time someone plays it. Until then, apps show
/// nothing, and Jellyseerr does not count the movie as available. After each scan, this tells Jellyfin what the
/// friend's catalog said, from the .nfo Bloom wrote. Playing it later replaces this with what Jellyfin probes.
/// </summary>
public sealed class FriendVideoInfo
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaStreamRepository _mediaStreams;
    private readonly CatalogService _catalog;
    private readonly ILogger<FriendVideoInfo> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendVideoInfo"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaStreams">Media stream repository.</param>
    /// <param name="catalog">Catalog service.</param>
    /// <param name="logger">Logger.</param>
    public FriendVideoInfo(ILibraryManager libraryManager, IMediaStreamRepository mediaStreams, CatalogService catalog, ILogger<FriendVideoInfo> logger)
    {
        _libraryManager = libraryManager;
        _mediaStreams = mediaStreams;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// Gives every friend's movie and episode that Jellyfin knows nothing about the video of the friend's copy.
    /// Must not run while other code edits the same items (see <see cref="VersionLinker"/>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of items updated.</returns>
    public async Task<int> FillAsync(CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            IncludeAlternateVersions = true,
            DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
        });

        var updated = 0;
        var failed = 0;
        foreach (var light in items.OfType<Video>().DistinctBy(v => v.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Probed (played once) or already filled: Jellyfin knows its size.
            if (light.Width > 0
                || light.Path is not string path
                || !_catalog.IsFederatedPath(path)
                || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (await FillOneAsync(light, path, cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken item must not stop the others (nor the scan's other tasks).
                _logger.LogDebug(ex, "Bloom: could not give Jellyfin the video details of {Path}", path);
                failed++;
            }
        }

        if (updated > 0)
        {
            _logger.LogInformation("Bloom: video details of {Count} friends' items given to Jellyfin", updated);
        }

        if (failed > 0)
        {
            _logger.LogWarning("Bloom: could not give Jellyfin the video details of {Count} friends' items", failed);
        }

        return updated;
    }

    private async Task<bool> FillOneAsync(Video light, string path, CancellationToken cancellationToken)
    {
        var nfo = Path.ChangeExtension(path, ".nfo");
        if (!File.Exists(nfo) || VideoDetails.Parse(await File.ReadAllTextAsync(nfo, cancellationToken).ConfigureAwait(false)) is not VideoDetails details)
        {
            return false;
        }

        // The list above is partial: only a full item may be saved.
        if (_libraryManager.GetItemById(light.Id) is not Video video)
        {
            return false;
        }

        var existing = _mediaStreams.GetMediaStreams(new MediaStreamQuery { ItemId = video.Id });
        if (existing.FirstOrDefault(s => s.Type == MediaStreamType.Video) is MediaStream known)
        {
            // Jellyfin has its own (probed) stream: only make sure the item carries its size.
            if (known.Width is not > 0 || video.Width > 0)
            {
                return false;
            }

            video.Width = known.Width.Value;
            video.Height = known.Height ?? 0;
        }
        else
        {
            // Our video stream first, then what Jellyfin found next to the .strm (the friend's subtitle files),
            // numbered the way Jellyfin numbers them.
            var streams = new List<MediaStream> { details.ToStream() };
            streams.AddRange(existing);
            for (var i = 0; i < streams.Count; i++)
            {
                streams[i].Index = i;
            }

            _mediaStreams.SaveMediaStreams(video.Id, streams, cancellationToken);
            video.Width = details.Width;
            video.Height = details.Height;
            video.DefaultVideoStreamIndex = 0;
        }

        await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
