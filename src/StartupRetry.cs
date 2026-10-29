using System;

namespace NativeTaskbarClock
{
    internal sealed class TaskbarNotReadyException : Exception
    {
        public TaskbarNotReadyException(string message) : base(message) { }
    }

    internal enum RetryOutcome { Applied, Waiting, Failed }

    internal sealed class StartupRetry
    {
        public bool Pending { get; private set; }

        public RetryOutcome Attempt(Action apply, out Exception failure)
        {
            failure = null;
            try { apply(); Pending = false; return RetryOutcome.Applied; }
            catch (TaskbarNotReadyException) { Pending = true; return RetryOutcome.Waiting; }
            catch (Exception ex) { Pending = false; failure = ex; return RetryOutcome.Failed; }
        }

        public void Cancel() { Pending = false; }
    }
}
