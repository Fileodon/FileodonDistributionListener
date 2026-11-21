using Renci.SshNet;
using Renci.SshNet.Common;

namespace FileodonDistributionListener.Services;

public sealed class TransferWorker(
    System.Threading.Channels.Channel<TransferWorkItem> queue,
    TransferJobStore store,
    ILogger<TransferWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (TransferWorkItem item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transfer job {JobId} failed", item.JobId);
                store.MarkFailed(item.JobId, ex);
            }
        }
    }

    private async Task ProcessAsync(TransferWorkItem item, CancellationToken cancellationToken)
    {
        store.MarkRunning(item.JobId);
        using SftpClient source = CreateClient(item.Request.Source);
        using SftpClient destination = CreateClient(item.Request.Destination);

        await source.ConnectAsync(cancellationToken);
        await destination.ConnectAsync(cancellationToken);

        try
        {
            for (int fileIndex = 0; fileIndex < item.Request.Files.Count; fileIndex++)
            {
                TransferFile file = item.Request.Files[fileIndex];
                try
                {
                    await using Stream sourceStream = source.OpenRead(file.SourceRemotePath);
                    await using Stream destinationStream = destination.OpenWrite(file.DestinationRemotePath);
                    await CopyWithProgressAsync(
                        sourceStream,
                        destinationStream,
                        file.Length,
                        uploaded => store.ReportProgress(item.JobId, fileIndex, uploaded),
                        cancellationToken);
                    store.MarkFileCompleted(item.JobId, fileIndex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    store.MarkFileFailed(item.JobId, fileIndex, ex);
                    throw;
                }
            }

            store.MarkCompleted(item.JobId);
        }
        finally
        {
            if (source.IsConnected) source.Disconnect();
            if (destination.IsConnected) destination.Disconnect();
        }
    }

    private static SftpClient CreateClient(SftpEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.PrivateKeyPem);

        var privateKeyBytes = System.Text.Encoding.UTF8.GetBytes(endpoint.PrivateKeyPem);
        using var privateKeyStream = new MemoryStream(privateKeyBytes);
        var privateKey = new PrivateKeyFile(privateKeyStream);
        var client = new SftpClient(
            endpoint.Host,
            endpoint.Port > 0 ? endpoint.Port : 22,
            endpoint.Username,
            privateKey);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        return client;
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256 * 1024];
        long uploaded = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            uploaded += read;
            report(uploaded);
        }
        await destination.FlushAsync(cancellationToken);
        if (uploaded == 0 && totalBytes > 0)
        {
            report(totalBytes);
        }
    }
}
