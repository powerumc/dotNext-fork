using System.Runtime.CompilerServices;
using Debug = System.Diagnostics.Debug;

namespace DotNext.Net.Cluster.Consensus.Raft;

using static Threading.AtomicInt64;

/// <summary>
/// Represents memory-based state machine with snapshotting support.
/// </summary>
/// <remarks>
/// The layout of the audit trail file system:
/// <list type="table">
/// <item>
/// <term>node.state</term>
/// <description>file containing internal state of Raft node</description>
/// </item>
/// <item>
/// <term>&lt;partition&gt;</term>
/// <description>file containing log partition with log records</description>
/// </item>
/// <item>
/// <term>snapshot</term>
/// <description>file containing snapshot</description>
/// </item>
/// </list>
/// The audit trail supports log compaction. However, it doesn't know how to interpret and reduce log records during compaction.
/// To do that, you can override <see cref="CreateSnapshotBuilder(in SnapshotBuilderContext)"/> method and implement state machine logic.
/// </remarks>
public abstract partial class MemoryBasedStateMachine : PersistentState
{
    private readonly CompactionMode compaction;
    private readonly bool replayOnInitialize, evictOnCommit;
    private readonly int snapshotBufferSize;
    private readonly Action<double>? compactionCounter;

    private long lastTerm;  // term of last committed entry, volatile

    // writer for this field must have exclusive async lock
    private Snapshot snapshot;

    /// <summary>
    /// Initializes a new persistent audit trail.
    /// </summary>
    /// <param name="path">The path to the folder to be used by audit trail.</param>
    /// <param name="recordsPerPartition">The maximum number of log entries that can be stored in the single file called partition.</param>
    /// <param name="configuration">The configuration of the persistent audit trail.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="recordsPerPartition"/> is less than 2.</exception>
    protected MemoryBasedStateMachine(DirectoryInfo path, int recordsPerPartition, Options? configuration)
        : base(path, recordsPerPartition, configuration ??= new())
    {
        compaction = configuration.CompactionMode;
        replayOnInitialize = configuration.ReplayOnInitialize;
        snapshotBufferSize = configuration.SnapshotBufferSize;
        evictOnCommit = configuration.CacheEvictionPolicy == LogEntryCacheEvictionPolicy.OnCommit;
        compactionCounter = ToDelegate(configuration.CompactionCounter);

        // initialize snapshot
        snapshot = new(path, snapshotBufferSize, in bufferManager, concurrentReads, writeThrough, initialSize: configuration.InitialPartitionSize);
        snapshot.Initialize();
    }

    /// <summary>
    /// Initializes a new persistent audit trail.
    /// </summary>
    /// <param name="path">The path to the folder to be used by audit trail.</param>
    /// <param name="recordsPerPartition">The maximum number of log entries that can be stored in the single file called partition.</param>
    /// <param name="configuration">The configuration of the persistent audit trail.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="recordsPerPartition"/> is less than 2.</exception>
    protected MemoryBasedStateMachine(string path, int recordsPerPartition, Options? configuration = null)
        : this(new DirectoryInfo(path), recordsPerPartition, configuration)
    {
    }

    private protected sealed override long LastTerm => lastTerm.VolatileRead();

    /// <summary>
    /// Gets a value indicating that log compaction should
    /// be called manually using <see cref="ForceCompactionAsync(long, CancellationToken)"/>
    /// in the background.
    /// </summary>
    public bool IsBackgroundCompaction => compaction == CompactionMode.Background;

    // this operation doesn't require write lock
    private async ValueTask BuildSnapshotAsync(int sessionId, long upperBoundIndex, SnapshotBuilder builder, CancellationToken token)
    {
        // Calculate the term of the snapshot
        Partition? current = LastPartition;
        builder.Term = this.TryGetPartition(upperBoundIndex, ref current)
            ? current.GetTerm(upperBoundIndex)
            : throw new MissingPartitionException(upperBoundIndex);

        // Initialize builder with snapshot record
        await builder.InitializeAsync(sessionId).ConfigureAwait(false);

        current = FirstPartition;
        Debug.Assert(current is not null);
        for (long startIndex = snapshot.Metadata.Index + 1L, currentIndex = startIndex; TryGetPartition(builder, startIndex, upperBoundIndex, ref currentIndex, ref current) && current is not null && startIndex <= upperBoundIndex; currentIndex++, token.ThrowIfCancellationRequested())
        {
            await ApplyIfNotEmptyAsync(builder, current.Read(sessionId, currentIndex)).ConfigureAwait(false);
        }

        // update counter
        compactionCounter?.Invoke(upperBoundIndex - snapshot.Metadata.Index);

        bool TryGetPartition(SnapshotBuilder builder, long startIndex, long endIndex, ref long currentIndex, ref Partition? partition)
        {
            builder.AdjustIndex(startIndex, endIndex, ref currentIndex);
            return currentIndex.IsBetween(startIndex, endIndex, BoundType.Closed) && this.TryGetPartition(currentIndex, ref partition);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ValueTask ApplyIfNotEmptyAsync(SnapshotBuilder builder, LogEntry entry)
            => entry.IsEmpty ? ValueTask.CompletedTask : builder.ApplyAsync(entry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCompactionRequired(long upperBoundIndex)
        => upperBoundIndex - Volatile.Read(ref snapshot).Metadata.Index >= recordsPerPartition;

    // In case of background compaction we need to have 1 fully committed partition as a divider
    // between partitions produced during writes and partitions to be compacted.
    // This restriction guarantees that compaction and writer thread will not be concurrent
    // when modifying Partition.next and Partition.previous fields need to keep sorted linked list
    // consistent and sorted.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetBackgroundCompactionCount(out long snapshotIndex)
    {
        snapshotIndex = Volatile.Read(ref snapshot).Metadata.Index;
        return Math.Max(((LastAppliedEntryIndex - snapshotIndex) / recordsPerPartition) - 1L, 0L);
    }

    /// <summary>
    /// Gets approximate number of partitions that can be compacted.
    /// </summary>
    public long CompactionCount
        => compaction == CompactionMode.Background ? GetBackgroundCompactionCount(out _) : 0L;

    /// <summary>
    /// Forces log compaction.
    /// </summary>
    /// <remarks>
    /// Full compaction may be time-expensive operation. In this case,
    /// all readers will be blocked until the end of the compaction.
    /// Therefore, <paramref name="count"/> can be used to reduce
    /// lock contention between compaction and readers. If it is <c>1</c>
    /// then compaction range is limited to the log entries contained in the single partition.
    /// This may be helpful if manual compaction is triggered by the background job.
    /// The job can wait for the commit using <see langword="WaitForCommitAsync(CancellationToken)"/>
    /// and then call this method with appropriate number of partitions to be collected
    /// according with <see cref="CompactionCount"/> property.
    /// </remarks>
    /// <param name="count">The number of partitions to be compacted.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous execution of this operation.</returns>
    /// <exception cref="ObjectDisposedException">This log is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    public ValueTask ForceCompactionAsync(long count, CancellationToken token)
    {
        ValueTask result;
        if (IsDisposed)
        {
            result = new(DisposedTask);
        }
        else if (count < 0L)
        {
            result = ValueTask.FromException(new ArgumentOutOfRangeException(nameof(count)));
        }
        else if (count == 0L || !IsBackgroundCompaction)
        {
            result = new();
        }
        else
        {
            result = ForceBackgroundCompactionAsync(count, token);
        }

        return result;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        long ComputeUpperBoundIndex(long count)
        {
            count = Math.Min(count, GetBackgroundCompactionCount(out var snapshotIndex));
            return checked((recordsPerPartition * count) + snapshotIndex);
        }

        async ValueTask ForceBackgroundCompactionAsync(long count, CancellationToken token)
        {
            Partition? removedHead;

            using (var builder = CreateSnapshotBuilder())
            {
                var upperBoundIndex = 0L;

                // initialize builder with log entries (read-only)
                await syncRoot.AcquireAsync(LockType.WeakReadLock, token).ConfigureAwait(false);
                var session = sessionManager.Take();
                try
                {
                    // check compaction range again because snapshot index can be modified by snapshot installation method
                    upperBoundIndex = ComputeUpperBoundIndex(count);
                    if (!IsCompactionRequired(upperBoundIndex))
                        return;

                    // construct snapshot (read-only operation)
                    await BuildSnapshotAsync(session, upperBoundIndex, builder, token).ConfigureAwait(false);
                }
                finally
                {
                    sessionManager.Return(session);
                    syncRoot.Release(LockType.WeakReadLock);
                }

                // rewrite snapshot as well as remove log entries (write access required)
                await syncRoot.AcquireAsync(LockType.CompactionLock, token).ConfigureAwait(false);
                try
                {
                    // Persist snapshot (cannot be canceled to avoid inconsistency)
                    await builder.BuildAsync(upperBoundIndex).ConfigureAwait(false);

                    // Remove squashed partitions
                    removedHead = DetachPartitions(upperBoundIndex);
                }
                finally
                {
                    syncRoot.Release(LockType.CompactionLock);
                }
            }

            DeletePartitions(removedHead);
        }
    }

    private protected sealed override async ValueTask<Partition?> InstallSnapshotAsync<TSnapshot>(TSnapshot snapshot, long snapshotIndex)
    {
        // Save the snapshot into temporary file to avoid corruption caused by network connection
        string tempSnapshotFile, snapshotFile = this.snapshot.FileName;
        using (var tempSnapshot = new Snapshot(Location, snapshotBufferSize, in bufferManager, 0, writeThrough, tempSnapshot: true, initialSize: SnapshotMetadata.Size + snapshot.Length.GetValueOrDefault()))
        {
            tempSnapshotFile = tempSnapshot.FileName;
            await tempSnapshot.WriteAsync(snapshot, snapshotIndex).ConfigureAwait(false);
            await tempSnapshot.FlushAsync().ConfigureAwait(false);
        }

        // Close existing snapshot file
        this.snapshot.Dispose();

        /*
         * Swapping snapshot file is unsafe operation because of potential disk I/O failures.
         * However, event if swapping will fail then it can be recovered manually just by renaming 'snapshot.new' file
         * into 'snapshot'. Both versions of snapshot file stay consistent. That's why stream copying is not an option.
         */
        try
        {
            File.Move(tempSnapshotFile, snapshotFile, true);
        }
        catch (Exception e)
        {
            Environment.FailFast(LogMessages.SnapshotInstallationFailed, e);
        }

        Volatile.Write(ref this.snapshot, await CreateSnapshotAsync().ConfigureAwait(false));

        // 5. Apply snapshot to the underlying state machine
        LastCommittedEntryIndex = snapshotIndex;
        LastUncommittedEntryIndex = Math.Max(snapshotIndex, LastUncommittedEntryIndex);

        var session = sessionManager.Take();
        try
        {
            await ApplyCoreAsync(this.snapshot.Read(session)).ConfigureAwait(false);
        }
        finally
        {
            sessionManager.Return(session);
        }

        lastTerm.VolatileWrite(snapshot.Term);
        LastAppliedEntryIndex = snapshotIndex;
        await PersistInternalStateAsync().ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
        OnCommit(1L);
        return DetachPartitions(snapshotIndex);

        async ValueTask<Snapshot> CreateSnapshotAsync()
        {
            var result = new Snapshot(Location, snapshotBufferSize, in bufferManager, concurrentReads, writeThrough);
            await result.InitializeAsync().ConfigureAwait(false);
            return result;
        }
    }

    private protected sealed override ValueTask<long> CommitAsync(long? endIndex, CancellationToken token)
    {
        // exclusive lock is required for sequential and foreground compaction;
        // otherwise - write lock which doesn't block background compaction
        return compaction switch
        {
            CompactionMode.Sequential => CommitAndCompactSequentiallyAsync(),
            CompactionMode.Foreground => CommitAndCompactInParallelAsync(),
            _ => CommitWithoutCompactionAsync(),
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        long GetCommitIndexAndCount(in long? endIndex, out long commitIndex)
        {
            var startIndex = LastCommittedEntryIndex + 1L;
            commitIndex = endIndex.HasValue ? Math.Min(LastUncommittedEntryIndex, endIndex.GetValueOrDefault()) : LastUncommittedEntryIndex;
            return commitIndex - startIndex + 1L;
        }

        async ValueTask<long> CommitAndCompactSequentiallyAsync()
        {
            Partition? removedHead;
            long count;
            await syncRoot.AcquireAsync(LockType.ExclusiveLock, token).ConfigureAwait(false);
            var session = sessionManager.Take();
            try
            {
                count = GetCommitIndexAndCount(in endIndex, out var commitIndex);
                if (count <= 0L)
                    return 0L;

                LastCommittedEntryIndex = commitIndex;
                await ApplyAsync(session, token).ConfigureAwait(false);
                removedHead = await ForceSequentialCompactionAsync(session, commitIndex, token).ConfigureAwait(false);
            }
            finally
            {
                sessionManager.Return(session);
                syncRoot.Release(LockType.ExclusiveLock);
            }

            OnCommit(count);
            DeletePartitions(removedHead);
            return count;
        }

        async ValueTask<Partition?> ForceSequentialCompactionAsync(int sessionId, long upperBoundIndex, CancellationToken token)
        {
            Partition? removedHead;
            if (IsCompactionRequired(upperBoundIndex))
            {
                using var builder = CreateSnapshotBuilder();
                await BuildSnapshotAsync(sessionId, upperBoundIndex, builder, token).ConfigureAwait(false);

                // Persist snapshot (cannot be canceled to avoid inconsistency)
                await builder.BuildAsync(upperBoundIndex).ConfigureAwait(false);

                // Remove squashed partitions
                removedHead = DetachPartitions(upperBoundIndex);
            }
            else
            {
                removedHead = null;
            }

            return removedHead;
        }

        async ValueTask<long> CommitWithoutCompactionAsync()
        {
            long count;
            await syncRoot.AcquireAsync(LockType.ExclusiveLock, token).ConfigureAwait(false);
            var session = sessionManager.Take();
            try
            {
                count = GetCommitIndexAndCount(in endIndex, out var commitIndex);
                if (count <= 0L)
                    return 0L;

                LastCommittedEntryIndex = commitIndex;
                await ApplyAsync(session, token).ConfigureAwait(false);
            }
            finally
            {
                sessionManager.Return(session);
                syncRoot.Release(LockType.ExclusiveLock);
            }

            OnCommit(count);
            return count;
        }

        async Task<Partition?> ForceIncrementalCompactionAsync(long upperBoundIndex, CancellationToken token)
        {
            Partition? removedHead;
            if (upperBoundIndex > 0L)
            {
                var builder = CreateSnapshotBuilder();
                var session = sessionManager.Take();
                try
                {
                    await BuildSnapshotAsync(session, upperBoundIndex, builder, token).ConfigureAwait(false);

                    // Persist snapshot (cannot be canceled to avoid inconsistency)
                    await builder.BuildAsync(upperBoundIndex).ConfigureAwait(false);

                    // Remove squashed partitions
                    removedHead = DetachPartitions(upperBoundIndex);
                }
                finally
                {
                    sessionManager.Return(session);
                    builder.Dispose();
                }
            }
            else
            {
                removedHead = null;
            }

            return removedHead;
        }

        async ValueTask<long> CommitAndCompactInParallelAsync()
        {
            Partition? removedHead;
            long count;
            await syncRoot.AcquireAsync(LockType.ExclusiveLock, token).ConfigureAwait(false);
            var session = sessionManager.Take();
            try
            {
                count = GetCommitIndexAndCount(in endIndex, out var commitIndex);
                if (count <= 0L)
                    return 0L;

                var compactionIndex = Math.Min(LastCommittedEntryIndex, snapshot.Metadata.Index + count);
                LastCommittedEntryIndex = commitIndex;
                var compaction = Task.Run(() => ForceIncrementalCompactionAsync(compactionIndex, token));
                try
                {
                    await ApplyAsync(session, token).ConfigureAwait(false);
                }
                finally
                {
                    removedHead = await compaction.ConfigureAwait(false);
                }
            }
            finally
            {
                sessionManager.Return(session);
                syncRoot.Release(LockType.ExclusiveLock);
            }

            OnCommit(count);
            DeletePartitions(removedHead);
            return count;
        }
    }

    /// <summary>
    /// Applies the command represented by the log entry to the underlying database engine.
    /// </summary>
    /// <param name="entry">The entry to be applied to the state machine.</param>
    /// <remarks>
    /// The base method does nothing so you don't need to call base implementation.
    /// </remarks>
    /// <returns>The task representing asynchronous execution of this method.</returns>
    /// <seealso cref="Commands.CommandInterpreter"/>
    protected abstract ValueTask ApplyAsync(LogEntry entry);

    private ValueTask ApplyCoreAsync(LogEntry entry) => entry.IsEmpty ? new() : ApplyAsync(entry); // skip empty log entry

    /// <summary>
    /// Flushes the underlying data storage.
    /// </summary>
    /// <returns>The task representing asynchronous execution of this method.</returns>
    protected virtual ValueTask FlushAsync() => new();

    private async ValueTask ApplyAsync(int sessionId, long startIndex, CancellationToken token)
    {
        var commitIndex = LastCommittedEntryIndex;
        for (Partition? partition = null; startIndex <= commitIndex; LastAppliedEntryIndex = startIndex++, token.ThrowIfCancellationRequested())
        {
            if (TryGetPartition(startIndex, ref partition))
            {
                var entry = partition.Read(sessionId, startIndex);
                await ApplyCoreAsync(entry).ConfigureAwait(false);
                lastTerm.VolatileWrite(entry.Term);

                // Remove log entry from the cache according to eviction policy
                if (entry.IsBuffered)
                {
                    await partition.PersistCachedEntryAsync(startIndex, entry.Position, evictOnCommit).ConfigureAwait(false);

                    // Flush partition if we are finished or at the last entry in it.
                    if (startIndex == commitIndex || startIndex == partition.LastIndex)
                        await partition.FlushAsync().ConfigureAwait(false);
                }
            }
            else
            {
                throw new MissingPartitionException(startIndex);
            }
        }

        await PersistInternalStateAsync(token).ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    private ValueTask ApplyAsync(int sessionId, CancellationToken token)
        => ApplyAsync(sessionId, LastAppliedEntryIndex + 1L, token);

    /// <summary>
    /// Reconstructs dataset by calling <see cref="ApplyAsync(LogEntry)"/>
    /// for each committed entry.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous state of the method.</returns>
    public async Task ReplayAsync(CancellationToken token = default)
    {
        ThrowIfDisposed();
        await syncRoot.AcquireAsync(LockType.ExclusiveLock, token).ConfigureAwait(false);
        var session = sessionManager.Take();
        try
        {
            LogEntry entry;
            long startIndex;

            // 1. Apply snapshot if it not empty
            if (!snapshot.IsEmpty)
            {
                entry = snapshot.Read(session);
                await ApplyCoreAsync(entry).ConfigureAwait(false);
                lastTerm.VolatileWrite(entry.Term);
                startIndex = snapshot.Metadata.Index;
            }
            else
            {
                startIndex = 0L;
            }

            // 2. Apply all committed entries
            await ApplyAsync(session, startIndex + 1L, token).ConfigureAwait(false);
        }
        finally
        {
            sessionManager.Return(session);
            syncRoot.Release(LockType.ExclusiveLock);
        }
    }

    /// <inheritdoc />
    public sealed override Task InitializeAsync(CancellationToken token = default)
    {
        if (token.IsCancellationRequested)
            return Task.FromCanceled(token);

        return replayOnInitialize ? ReplayAsync(token) : Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            snapshot.Dispose();
        }

        base.Dispose(disposing);
    }
}