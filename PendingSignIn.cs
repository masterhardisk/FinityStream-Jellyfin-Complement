namespace Jellyfin.Plugin.FinityStream;

internal sealed class PendingSignIn(string secret)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _approved;
    public string Secret { get; } = secret;

    public async Task<string> ResolveAsync(bool approve, Func<Task<string>> resolve, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_approved)
            {
                return "alreadyApproved";
            }

            var state = await resolve();
            _approved = state == "alreadyApproved" || (approve && state == "approved");
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }
}
