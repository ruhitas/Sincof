using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Infrastructure.Disk
{
    public class DiskTestQueue : IDiskTestQueue
    {
        private readonly Channel<DiskTestRequest> _queue;

        public DiskTestQueue(int capacity = 100)
        {
            // Bounded channel to prevent memory overflow
            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true, // We will consume from a single background loop
                SingleWriter = false // Multiple producers might exist
            };
            _queue = Channel.CreateBounded<DiskTestRequest>(options);
        }

        public async ValueTask EnqueueAsync(DiskTestRequest request, CancellationToken ct = default)
        {
            await _queue.Writer.WriteAsync(request, ct);
        }

        public async ValueTask<DiskTestRequest> DequeueAsync(CancellationToken ct = default)
        {
            return await _queue.Reader.ReadAsync(ct);
        }
    }
}
