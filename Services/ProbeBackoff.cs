using System;

namespace ModelDesk.Services;

public static class ProbeBackoff
{
    public static TimeSpan DelayForFailure(int consecutiveFailures) => TimeSpan.FromSeconds(consecutiveFailures <= 1 ? 15 : consecutiveFailures == 2 ? 30 : 60);

    public static bool ShouldDeferForPrompt(PromptProgress? progress, DateTimeOffset now, DateTimeOffset lastAttempt)
        => progress != null && now >= progress.ObservedAt && now - progress.ObservedAt < TimeSpan.FromSeconds(15)
            && now >= lastAttempt && now - lastAttempt < TimeSpan.FromSeconds(15);
}
