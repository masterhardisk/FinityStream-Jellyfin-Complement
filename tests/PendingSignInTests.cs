using Jellyfin.Plugin.FinityStream;
using Xunit;

public sealed class PendingSignInTests
{
    [Fact]
    public async Task ConcurrentApprovalIsAppliedOnce()
    {
        var request = new PendingSignIn("secret");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        async Task<string> Approve()
        {
            Interlocked.Increment(ref attempts);
            started.TrySetResult();
            await release.Task;
            return "approved";
        }

        var first = request.ResolveAsync(true, Approve, CancellationToken.None);
        await started.Task;
        var second = request.ResolveAsync(true, Approve, CancellationToken.None);
        release.SetResult();

        Assert.Equal("approved", await first);
        Assert.Equal("alreadyApproved", await second);
        Assert.Equal(1, attempts);
        Assert.Equal("alreadyApproved", await request.ResolveAsync(false,
            () => Task.FromResult("expired"), CancellationToken.None));
    }

    [Fact]
    public async Task PendingAndExpiredResultsDoNotMarkTheRequestApproved()
    {
        var request = new PendingSignIn("secret");
        Assert.Equal("pending", await request.ResolveAsync(false,
            () => Task.FromResult("pending"), CancellationToken.None));
        Assert.Equal("expired", await request.ResolveAsync(true,
            () => Task.FromResult("expired"), CancellationToken.None));
    }
}
