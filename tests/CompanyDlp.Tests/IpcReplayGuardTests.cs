using CompanyDlp.Service;
using Xunit;

namespace CompanyDlp.Tests;

// Extracted from PipeServer.TryAcceptMessage (see IpcReplayGuard.cs) specifically so this real
// per-connection anti-replay defense - every IPC message an authenticated local client sends goes
// through it - is directly testable without constructing a full PipeServer.
public sealed class IpcReplayGuardTests
{
    [Fact]
    public void FirstMessage_WithinWindow_IsAccepted()
    {
        var guard = new IpcReplayGuard();
        var now = DateTimeOffset.UtcNow;

        var accepted = guard.TryAccept(Guid.NewGuid(), now, now, out var failure);

        Assert.True(accepted);
        Assert.Equal("", failure);
    }

    [Fact]
    public void DuplicateMessageId_WithinWindow_IsRejected()
    {
        var guard = new IpcReplayGuard();
        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid();
        Assert.True(guard.TryAccept(messageId, now, now, out _));

        var accepted = guard.TryAccept(messageId, now, now.AddSeconds(1), out var failure);

        Assert.False(accepted);
        Assert.Equal("Duplicate IPC message rejected.", failure);
    }

    [Fact]
    public void MessageOlderThanWindow_IsRejected()
    {
        var guard = new IpcReplayGuard();
        var now = DateTimeOffset.UtcNow;
        var sentAtUtc = now.AddMinutes(-6);

        var accepted = guard.TryAccept(Guid.NewGuid(), sentAtUtc, now, out var failure);

        Assert.False(accepted);
        Assert.Equal("IPC message timestamp is outside the accepted window.", failure);
    }

    // The window check uses .Duration() (absolute value), so a message timestamped implausibly in the
    // FUTURE relative to the server's clock is rejected the same way a stale replayed one is - not
    // just "too old" is checked.
    [Fact]
    public void MessageTooFarInFuture_IsRejected()
    {
        var guard = new IpcReplayGuard();
        var now = DateTimeOffset.UtcNow;
        var sentAtUtc = now.AddMinutes(6);

        var accepted = guard.TryAccept(Guid.NewGuid(), sentAtUtc, now, out var failure);

        Assert.False(accepted);
        Assert.Equal("IPC message timestamp is outside the accepted window.", failure);
    }

    [Fact]
    public void MessageJustInsideWindow_IsAccepted()
    {
        var guard = new IpcReplayGuard();
        var now = DateTimeOffset.UtcNow;
        var sentAtUtc = now.AddMinutes(-4).AddSeconds(-59);

        var accepted = guard.TryAccept(Guid.NewGuid(), sentAtUtc, now, out _);

        Assert.True(accepted);
    }

    [Fact]
    public void EmptyMessageId_IsRejected()
    {
        var guard = new IpcReplayGuard();
        var now = DateTimeOffset.UtcNow;

        var accepted = guard.TryAccept(Guid.Empty, now, now, out var failure);

        Assert.False(accepted);
        Assert.Equal("IPC messageId is required.", failure);
    }

    [Fact]
    public void DefaultSentAtUtc_IsRejected()
    {
        var guard = new IpcReplayGuard();

        var accepted = guard.TryAccept(Guid.NewGuid(), default, DateTimeOffset.UtcNow, out var failure);

        Assert.False(accepted);
        Assert.Equal("IPC message timestamp is outside the accepted window.", failure);
    }

    // A custom window is honored - not hardcoded to the production 5-minute default.
    [Fact]
    public void CustomWindow_IsHonored()
    {
        var guard = new IpcReplayGuard(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;

        Assert.False(guard.TryAccept(Guid.NewGuid(), now.AddSeconds(-31), now, out _));
        Assert.True(guard.TryAccept(Guid.NewGuid(), now.AddSeconds(-29), now, out _));
    }

    [Fact]
    public void DifferentMessageIds_BothAccepted_TrackedCountIncreases()
    {
        var guard = new IpcReplayGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept(Guid.NewGuid(), now, now, out _));
        Assert.True(guard.TryAccept(Guid.NewGuid(), now, now, out _));

        Assert.Equal(2, guard.TrackedMessageCount);
    }
}
