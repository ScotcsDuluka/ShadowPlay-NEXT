// Host-only wait and cleanup policy for the Windows remote loader.
// Never free the remote Unicode path while LoadLibraryW may still read it.
internal enum RemoteLoaderWaitOutcome
{
    Completed,
    TimedOut,
    Failed
}
internal static class RemoteLoaderWaitPolicy
{
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;
    internal const uint LoadTimeoutMs = 10000;
    internal static RemoteLoaderWaitOutcome Classify(uint waitCode) =>
        waitCode switch
        {
            WaitObject0 => RemoteLoaderWaitOutcome.Completed,
            WaitTimeout => RemoteLoaderWaitOutcome.TimedOut,
            _ => RemoteLoaderWaitOutcome.Failed
        };
    internal static bool CanReleaseRemoteArguments(uint lastPoll) =>
        Classify(lastPoll) == RemoteLoaderWaitOutcome.Completed;
}
