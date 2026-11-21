using System.Threading.Channels;

namespace FileodonDistributionListener.Services;

public sealed class TransferQueue(Channel<TransferWorkItem> channel)
{
    public ValueTask EnqueueAsync(TransferWorkItem item, CancellationToken cancellationToken) =>
        channel.Writer.WriteAsync(item, cancellationToken);
}
