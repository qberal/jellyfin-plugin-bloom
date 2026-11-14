using Jellyfin.Plugin.Bloom.Services;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public sealed class LibrarySummaryTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid D = Guid.NewGuid();
    private static readonly Guid E = Guid.NewGuid();
    private static readonly Guid F = Guid.NewGuid();

    private static SummaryItem Video(Guid id, string? peer = null, Guid? versionOf = null) => new(id, peer, versionOf, []);

    private static SummaryItem Show(Guid id, string? peer, params string[] keys) => new(id, peer, null, keys);

    [Fact]
    public void MoviesAddUpLikeJellyfinsDashboardAndTellWhatIsNew()
    {
        var count = LibrarySummary.CountVideos([
            Video(A),                           // ours
            Video(B),                           // ours
            Video(C, "alice", versionOf: A),    // Alice's 4K copy of one of ours: not new
            Video(D, "alice"),                  // only Alice has it
            Video(E, "carol", versionOf: D),    // Carol has it too: still one new movie
            Video(F, "carol")                   // only Carol has it
        ]);

        // Jellyfin's dashboard says 6: every copy.
        Assert.Equal(6, count.Total);
        Assert.Equal(2, count.Own);
        Assert.Equal(4, count.Friends);
        Assert.Equal(2, count.New);
        Assert.Equal(2, count.ByFriend["alice"]);
        Assert.Equal(2, count.ByFriend["carol"]);
    }

    [Fact]
    public void AVersionOfAMissingItemCountsAsNew()
    {
        // The movie it was a version of is gone (removed meanwhile): it stands alone.
        var count = LibrarySummary.CountVideos([Video(A, "alice", versionOf: Guid.NewGuid())]);

        Assert.Equal(1, count.New);
    }

    [Fact]
    public void ShowsWeAlsoHaveAreNotNew()
    {
        var count = LibrarySummary.CountSeries([
            Show(A, null, "tvdb:81189", "tmdb:1396"),   // ours
            Show(B, "alice", "tvdb:81189"),              // Alice brings episodes of a show we have
            Show(C, "alice", "tvdb:73244"),              // only Alice has it
            Show(D, "carol", "tmdb:2316", "tvdb:73244"), // Carol too (same show): one new show
            Show(E, "carol")                             // no id at all: counted on its own
        ]);

        Assert.Equal(5, count.Total);
        Assert.Equal(1, count.Own);
        Assert.Equal(4, count.Friends);
        Assert.Equal(2, count.New);
        Assert.Equal(2, count.ByFriend["alice"]);
        Assert.Equal(2, count.ByFriend["carol"]);
    }
}
