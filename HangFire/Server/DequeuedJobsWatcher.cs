﻿using System;
using System.Threading;
using HangFire.States;
using ServiceStack.Logging;
using ServiceStack.Redis;

namespace HangFire.Server
{
    internal class DequeuedJobsWatcher : IThreadWrappable, IDisposable
    {
        private static readonly TimeSpan CheckedTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SleepTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(15);

        private readonly IRedisClient _redis;
        private readonly StateMachine _stateMachine;

        private readonly ManualResetEvent _stopped = new ManualResetEvent(false);

        private readonly ILog _logger = LogManager.GetLogger(typeof(DequeuedJobsWatcher));

        public DequeuedJobsWatcher(IRedisClientsManager redisManager)
        {
            _redis = redisManager.GetClient();
            _stateMachine = new StateMachine(_redis);
        }

        public void Dispose()
        {
            _redis.Dispose();
        }

        public void FindAndRequeueTimedOutJobs()
        {
            var queues = _redis.GetAllItemsFromSet("hangfire:queues");

            foreach (var queue in queues)
            {
                using (_redis.AcquireLock(
                    String.Format("hangfire:queue:{0}:dequeued:lock", queue),
                    TimeSpan.FromMinutes(1)))
                {
                    var jobIds = _redis.GetAllItemsFromList(
                        String.Format("hangfire:queue:{0}:dequeued", queue));

                    foreach (var jobId in jobIds)
                    {
                        RequeueJobIfTimedOut(jobId, queue);
                    }
                }
            }
        }

        private void RequeueJobIfTimedOut(string jobId, string queue)
        {
            var flags = _redis.GetValuesFromHash(
                String.Format("hangfire:job:{0}", jobId),
                "Fetched",
                "Checked");

            var fetched = flags[0];
            var @checked = flags[1];

            if (String.IsNullOrEmpty(fetched) && String.IsNullOrEmpty(@checked))
            {
                // If the job does not have these flags set, then it is
                // in the implicit 'Dequeued' state. This state has no 
                // information about the time it was dequeued. So we
                // can not do anything with the job in this state, because
                // there are two options:

                // 1. It is going to move to the implicit 'Fetched' state
                //    in a short time.
                // 2. It will stay in the 'Dequeued' state forever due to
                //    its processing server is dead.

                // To ensure its server is dead, we'll move the job to
                // the implicit 'Checked' state with the current timestamp
                // and will not do anything else at this pass of the watcher.
                // If job's state will still be 'Checked' on the later passes
                // and after the CheckedTimeout expired, then the server
                // is dead, and we'll re-queue the job.

                _redis.SetEntryInHash(
                    String.Format("hangfire:job:{0}", jobId),
                    "Checked",
                    JobHelper.ToStringTimestamp(DateTime.UtcNow));

                // Checkpoint #1-2. The job is in the implicit 'Checked' state.
                // It will be re-queued after the CheckedTimeout will be expired.
            }
            else
            {
                if (TimedOutByFetchedTime(fetched) || TimedOutByCheckedTime(fetched, @checked))
                {
                    var state = new EnqueuedState("Requeued due to time out");
                    _stateMachine.ChangeState(jobId, state, EnqueuedState.Name, ProcessingState.Name);

                    JobFetcher.RemoveFromFetchedQueue(_redis, jobId, queue);
                }
            }
        }

        private static bool TimedOutByFetchedTime(string fetchedTimestamp)
        {
            return !String.IsNullOrEmpty(fetchedTimestamp) &&
                   (DateTime.UtcNow - JobHelper.FromStringTimestamp(fetchedTimestamp) > JobTimeout);
        }

        private static bool TimedOutByCheckedTime(string fetchedTimestamp, string checkedTimestamp)
        {
            // If the job has the 'fetched' flag set, then it is
            // in the implicit 'Fetched' state, and it can not be timed
            // out by the 'checked' flag.
            if (!String.IsNullOrEmpty(fetchedTimestamp))
            {
                return false;
            }

            return !String.IsNullOrEmpty(checkedTimestamp) &&
                   (DateTime.UtcNow - JobHelper.FromStringTimestamp(checkedTimestamp) > CheckedTimeout);
        }

        void IThreadWrappable.Work()
        {
            try
            {
                while (true)
                {
                    FindAndRequeueTimedOutJobs();

                    if (_stopped.WaitOne(SleepTimeout))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Fatal(
                    "Unexpected exception caught in the timed out jobs thread. Timed out jobs will not be re-queued.",
                    ex);
            }
        }

        void IThreadWrappable.Dispose(Thread thread)
        {
            _stopped.Set();
            thread.Join();
        }
    }
}
