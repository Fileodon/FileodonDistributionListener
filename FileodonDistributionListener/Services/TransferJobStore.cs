using System.Collections.Concurrent;

namespace FileodonDistributionListener.Services;

public sealed class TransferJobStore
{
    private sealed class JobState
    {
        public readonly object Lock = new();
        public required Guid JobId { get; init; }
        public required TransferJobStatus StatusValue { get; set; }
        public required IReadOnlyList<TransferFileProgress> FilesValue { get; set; }
        public required long TotalBytes { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CompletedAtValue { get; set; }
        public string? ErrorValue { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    public TransferJobSnapshot Create(TransferRequest request)
    {
        Guid jobId = Guid.NewGuid();
        var files = request.Files.Select(file => new TransferFileProgress(
            file.SourceRemotePath,
            file.DestinationRemotePath,
            file.Length,
            0,
            false,
            null)).ToArray();
        var state = new JobState
        {
            JobId = jobId,
            StatusValue = TransferJobStatus.Queued,
            FilesValue = files,
            TotalBytes = request.Files.Sum(file => file.Length),
            CreatedAt = DateTimeOffset.UtcNow
        };
        if (!_jobs.TryAdd(jobId, state))
        {
            throw new InvalidOperationException("The transfer job could not be created.");
        }

        return Snapshot(state);
    }

    public void MarkRunning(Guid jobId) =>
        Update(jobId, state => state.StatusValue = TransferJobStatus.Running);

    public void ReportProgress(Guid jobId, int fileIndex, long uploadedBytes)
    {
        Update(jobId, state =>
        {
            var files = (TransferFileProgress[])state.FilesValue;
            var current = files[fileIndex];
            files[fileIndex] = current with
            {
                UploadedBytes = Math.Min(current.TotalBytes, Math.Max(current.UploadedBytes, uploadedBytes))
            };
        });
    }

    public void MarkFileCompleted(Guid jobId, int fileIndex)
    {
        Update(jobId, state =>
        {
            var files = (TransferFileProgress[])state.FilesValue;
            var current = files[fileIndex];
            files[fileIndex] = current with
            {
                UploadedBytes = current.TotalBytes,
                Completed = true
            };
        });
    }

    public void MarkCompleted(Guid jobId) =>
        Update(jobId, state =>
        {
            state.StatusValue = TransferJobStatus.Completed;
            state.CompletedAtValue = DateTimeOffset.UtcNow;
        });

    public void MarkFailed(Guid jobId, Exception exception) =>
        Update(jobId, state =>
        {
            state.StatusValue = TransferJobStatus.Failed;
            state.ErrorValue = exception.Message;
            state.CompletedAtValue = DateTimeOffset.UtcNow;
        });

    public void MarkFileFailed(Guid jobId, int fileIndex, Exception exception) =>
        Update(jobId, state =>
        {
            var files = (TransferFileProgress[])state.FilesValue;
            files[fileIndex] = files[fileIndex] with { Error = exception.Message };
        });

    public TransferJobSnapshot? Get(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out JobState? state) ? Snapshot(state) : null;
    }

    private void Update(Guid jobId, Action<JobState> update)
    {
        if (!_jobs.TryGetValue(jobId, out JobState? state))
        {
            throw new KeyNotFoundException($"Transfer job {jobId} was not found.");
        }

        lock (state.Lock)
        {
            update(state);
        }
    }

    private static TransferJobSnapshot Snapshot(JobState state)
    {
        lock (state.Lock)
        {
            IReadOnlyList<TransferFileProgress> files = state.FilesValue;
            long uploadedBytes = files.Sum(file => file.UploadedBytes);
            double percentage = state.TotalBytes == 0
                ? 100
                : uploadedBytes * 100d / state.TotalBytes;
            return new TransferJobSnapshot(
                state.JobId,
                state.StatusValue,
                files.Count,
                state.TotalBytes,
                uploadedBytes,
                percentage,
                files,
                state.ErrorValue,
                state.CreatedAt,
                state.CompletedAtValue);
        }
    }
}
