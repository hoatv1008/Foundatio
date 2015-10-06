﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Queues;
using Foundatio.Utility;
using Foundatio.Logging;

namespace Foundatio.Jobs {
    public abstract class QueueProcessorJobBase<T> : JobBase, IQueueProcessorJob where T : class {
        protected readonly IQueue<T> _queue;

        public QueueProcessorJobBase(IQueue<T> queue) {
            _queue = queue;
            AutoComplete = true;
        }

        protected bool AutoComplete { get; set; }

        protected override async Task<JobResult> RunInternalAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TimeSpan.FromSeconds(30).ToCancellationToken());

            QueueEntry<T> queueEntry;
            try {
                queueEntry = await _queue.DequeueAsync(linkedCancellationToken.Token).AnyContext();
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message($"Error trying to dequeue message: {ex.Message}").Write();
                return JobResult.FromException(ex);
            }

            if (queueEntry == null)
                return JobResult.Success;

            if (cancellationToken.IsCancellationRequested) {
                Logger.Info().Message($"Job was cancelled. Abandoning queue item: {queueEntry.Id}").Write();
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.Cancelled;
            }

            using (var lockValue = await GetQueueEntryLockAsync(queueEntry, cancellationToken).AnyContext()) {
                if (lockValue == null)
                    return JobResult.SuccessWithMessage("Unable to acquire queue item lock.");

                Logger.Trace().Message("Processing queue entry '{0}'.", queueEntry.Id).Write();
                try {
                    var result = await ProcessQueueEntryAsync(new JobQueueEntryContext<T>(queueEntry, lockValue, cancellationToken)).AnyContext();

                    if (!AutoComplete)
                        return result;

                    if (result.IsSuccess)
                        await queueEntry.CompleteAsync().AnyContext();
                    else
                        await queueEntry.AbandonAsync().AnyContext();

                    return result;
                } catch {
                    await queueEntry.AbandonAsync().AnyContext();
                    throw;
                }
            }
        }

        protected virtual Task<ILock> GetQueueEntryLockAsync(QueueEntry<T> queueEntry, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.EmptyLock);
        }
        
        public async Task RunUntilEmptyAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            await RunContinuousAsync(cancellationToken: cancellationToken, continuationCallback: async () => {
                var stats = await _queue.GetQueueStatsAsync().AnyContext();
                Logger.Trace().Message("RunUntilEmpty continuation: queue: {0} working={1}", stats.Queued, stats.Working).Write();
                return stats.Queued + stats.Working > 0;
            }).AnyContext();
        }

        protected abstract Task<JobResult> ProcessQueueEntryAsync(JobQueueEntryContext<T> context);
    }
    
    public class JobQueueEntryContext<T> where T : class {
        public JobQueueEntryContext(QueueEntry<T> queueEntry, ILock queueEntryLock, CancellationToken cancellationToken = default(CancellationToken)) {
            QueueEntry = queueEntry;
            QueueEntryLock = queueEntryLock;
            CancellationToken = cancellationToken;
        }

        public QueueEntry<T> QueueEntry { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public ILock QueueEntryLock { get; private set; }
    }

    public interface IQueueProcessorJob : IDisposable {
        Task RunUntilEmptyAsync(CancellationToken cancellationToken = default(CancellationToken));
    }
}
