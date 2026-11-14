using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Bloom.Tasks;

/// <summary>
/// Periodic sync of every friend (catches anything a change notification missed).
/// </summary>
public class FederationSyncTask : IScheduledTask
{
    private readonly SyncService _sync;
    private readonly ArrSync _arr;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationSyncTask"/> class.
    /// </summary>
    /// <param name="sync">Sync service.</param>
    /// <param name="arr">Tells Radarr and Sonarr what friends have.</param>
    public FederationSyncTask(SyncService sync, ArrSync arr)
    {
        _sync = sync;
        _arr = arr;
    }

    /// <inheritdoc />
    public string Name => Text.Get("TaskName");

    /// <inheritdoc />
    public string Key => "BloomSync";

    /// <inheritdoc />
    public string Description => Text.Get("TaskDescription");

    /// <inheritdoc />
    public string Category => "Bloom";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _sync.SyncAllAsync(progress, cancellationToken).ConfigureAwait(false);

        // Radarr and Sonarr change on their own (a movie requested meanwhile): check them even if no friend changed.
        _arr.Queue();
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
