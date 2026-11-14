using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bloom.Models;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public sealed class VideoDetailsTests
{
    private static CatalogItem Item(string type, int? width, int? height, string? range = null, string? codec = null) => new()
    {
        Id = "aaaaaaaaaaaa",
        Type = type,
        Name = "Dune",
        Year = 2021,
        ParentIndexNumber = type == "Episode" ? 1 : null,
        IndexNumber = type == "Episode" ? 2 : null,
        Width = width,
        Height = height,
        VideoRange = range,
        VideoCodec = codec
    };

    public static TheoryData<VideoRangeType> Ranges()
    {
        var data = new TheoryData<VideoRangeType>();
        foreach (var range in Enum.GetValues<VideoRangeType>().Where(r => r != VideoRangeType.Unknown))
        {
            data.Add(range);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void JellyfinFindsTheFriendsRangeInTheStream(VideoRangeType range)
    {
        var stream = new VideoDetails("hevc", 3840, 2160, range).ToStream();

        Assert.Equal(MediaStreamType.Video, stream.Type);
        Assert.Equal(range, stream.VideoRangeType);
        Assert.Equal(3840, stream.Width);
        Assert.Equal(2160, stream.Height);
        Assert.Equal("hevc", stream.Codec);
        Assert.True(stream.IsDefault);
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void MovieNfoCarriesTheVideoDetails(VideoRangeType range)
    {
        var nfo = NfoBuilder.Movie(Item("Movie", 3840, 1608, range.ToString(), "HEVC"), "Alice");

        Assert.Equal(new VideoDetails("hevc", 3840, 1608, range), VideoDetails.Parse(nfo));
    }

    [Fact]
    public void NfoUsesKodisStreamDetails()
    {
        var root = XDocument.Parse(NfoBuilder.Movie(Item("Movie", 3840, 2160, "DOVIWithHDR10", "hevc"), "Alice")).Root!;
        var video = root.Element("fileinfo")!.Element("streamdetails")!.Element("video")!;

        Assert.Equal("hevc", video.Element("codec")!.Value);
        Assert.Equal("3840", video.Element("width")!.Value);
        Assert.Equal("2160", video.Element("height")!.Value);
        Assert.Equal("dolbyvision", video.Element("hdrtype")!.Value);
        Assert.Equal("DOVIWithHDR10", video.Element("videorangetype")!.Value);

        // SDR: no HDR type at all.
        video = XDocument.Parse(NfoBuilder.Movie(Item("Movie", 1920, 1080, "SDR", "h264"), "Alice")).Root!.Element("fileinfo")!.Element("streamdetails")!.Element("video")!;
        Assert.Null(video.Element("hdrtype"));
        Assert.Null(video.Element("videorangetype"));
    }

    [Fact]
    public void EpisodeNfoCarriesTheVideoDetails()
    {
        var nfo = NfoBuilder.Episode(Item("Episode", 1280, 720, "SDR", "h264"), "Show");

        Assert.Equal(new VideoDetails("h264", 1280, 720, VideoRangeType.SDR), VideoDetails.Parse(nfo));
    }

    [Fact]
    public void NothingIsWrittenWithoutTheSizeOfTheVideo()
    {
        // Older friends, or a file their Jellyfin never probed.
        foreach (var item in new[] { Item("Movie", null, null, "HDR10", "hevc"), Item("Movie", 1920, null), Item("Movie", 0, 0) })
        {
            var nfo = NfoBuilder.Movie(item, "Alice");
            Assert.Null(XDocument.Parse(nfo).Root!.Element("fileinfo"));
            Assert.Null(VideoDetails.Parse(nfo));
        }
    }

    [Fact]
    public void WhatAFriendSendsIsCleanedUp()
    {
        var details = VideoDetails.From(Item("Movie", 1920, 1080, "NotARange", " H.264 <script>"));

        Assert.NotNull(details);
        Assert.Equal(VideoRangeType.SDR, details.Range);
        Assert.Equal("h.264script", details.Codec);
        Assert.Equal(VideoRangeType.SDR, VideoDetails.From(Item("Movie", 1920, 1080, "999"))!.Range);
    }

    [Fact]
    public void ReadingIsRobust()
    {
        Assert.Null(VideoDetails.Parse("not xml"));
        Assert.Null(VideoDetails.Parse("<movie><title>x</title></movie>"));
        Assert.Null(VideoDetails.Parse("<movie><fileinfo><streamdetails><video><width>abc</width><height>1080</height></video></streamdetails></fileinfo></movie>"));
        Assert.Null(VideoDetails.Parse("<movie><fileinfo><streamdetails><video><width>-1920</width><height>1080</height></video></streamdetails></fileinfo></movie>"));
        Assert.Null(VideoDetails.Parse("<movie><fileinfo><streamdetails><video><width>99999999</width><height>1080</height></video></streamdetails></fileinfo></movie>"));

        // No DTD, so no entity tricks.
        Assert.Null(VideoDetails.Parse("<!DOCTYPE movie [<!ENTITY w \"1920\">]><movie><fileinfo><streamdetails><video><width>&w;</width><height>1080</height></video></streamdetails></fileinfo></movie>"));

        // An .nfo written by Kodi or another tool: no exact range, Kodi's HDR type is used.
        var kodi = VideoDetails.Parse("<movie><fileinfo><streamdetails><video><codec>hevc</codec><width> 3840 </width><height>2160</height><hdrtype>dolbyvision</hdrtype></video></streamdetails></fileinfo></movie>");
        Assert.Equal(new VideoDetails("hevc", 3840, 2160, VideoRangeType.DOVI), kodi);
    }
}
