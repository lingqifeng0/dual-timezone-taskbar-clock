using System;
using NativeTaskbarClock;

internal static class StartupRetryTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Main()
    {
        StartupRetry retry = new StartupRetry();
        int attempts = 0;
        Action delayed = delegate {
            attempts++;
            if (attempts < 3) throw new TaskbarNotReadyException("not ready");
        };
        Exception failure;
        Check(retry.Attempt(delayed, out failure) == RetryOutcome.Waiting && retry.Pending && failure == null, "first wait");
        Check(retry.Attempt(delayed, out failure) == RetryOutcome.Waiting && retry.Pending && failure == null, "second wait");
        Check(retry.Attempt(delayed, out failure) == RetryOutcome.Applied && !retry.Pending && failure == null, "eventual success");
        Check(retry.Attempt(delegate { throw new InvalidOperationException("unsupported"); }, out failure) == RetryOutcome.Failed && !retry.Pending && failure.Message == "unsupported", "permanent failure");
        Check(retry.Attempt(delegate { throw new TaskbarNotReadyException("later"); }, out failure) == RetryOutcome.Waiting, "retry again");
        retry.Cancel();
        Check(!retry.Pending, "cancel");
        Console.WriteLine("startup retry tests passed");
    }
}
