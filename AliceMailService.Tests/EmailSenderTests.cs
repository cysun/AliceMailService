using MailKit.Security;
using NSubstitute;

namespace AliceMailService.Tests;

public class EmailSenderTests
{
    [Fact]
    public async Task PacesAcrossBatchesAndIgnoresWallClockChanges()
    {
        var f = new SenderFixture();
        await f.Clock.CompleteAsync(f.Sender.SendAsync([SenderFixture.Message("a"), SenderFixture.Message("b")], default));
        f.Clock.UtcOffset = TimeSpan.FromDays(-365);
        await f.Clock.CompleteAsync(f.Sender.SendAsync([SenderFixture.Message("c"), SenderFixture.Message("d")], default));

        Assert.Equal(new long[] { 0, 1, 2, 3 }.Select(seconds => seconds * TimeSpan.TicksPerSecond),
            f.Attempts.Select(attempt => attempt.Timestamp));
    }

    [Fact]
    public async Task DoesNotAccumulateBurstCapacityWhileIdle()
    {
        var f = new SenderFixture();
        await f.Sender.SendAsync([SenderFixture.Message()], default);
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        await f.Clock.CompleteAsync(f.Sender.SendAsync([SenderFixture.Message("b"), SenderFixture.Message("c")], default));
        Assert.Equal(new long[] { 0, 20, 21 }.Select(seconds => seconds * TimeSpan.TicksPerSecond),
            f.Attempts.Select(attempt => attempt.Timestamp));
    }

    [Fact]
    public async Task SlowSendsDoNotRequireAnAdditionalFullInterval()
    {
        var f = new SenderFixture();
        f.OnSend = (_, _) => { f.Clock.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult("accepted"); };
        await f.Clock.CompleteAsync(f.Sender.SendAsync([SenderFixture.Message("a"), SenderFixture.Message("b")], default));
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, f.Attempts[1].Timestamp - f.Attempts[0].Timestamp);
        Assert.Empty(f.Clock.Delays);
    }

    [Fact]
    public async Task RetriesSameMessageBeforeContinuingAndCountsHiddenRecipientsWithoutLoggingAddresses()
    {
        var f = new SenderFixture();
        f.OnSend = (_, _) => f.Attempts.Count <= 2
            ? Task.FromException<string>(SenderFixture.Rejection()) : Task.FromResult("accepted");
        var task = f.Sender.SendAsync([SenderFixture.Message("a", true), SenderFixture.Message("b")], default);
        await f.Clock.CompleteAsync(task);

        Assert.Empty(await task);
        Assert.Equal(new[] { "a@example.test", "a@example.test", "a@example.test", "b@example.test" },
            f.Attempts.Select(attempt => attempt.Id));
        Assert.InRange(f.Clock.Delays[0].TotalSeconds, 5, 6);
        Assert.InRange(f.Clock.Delays[1].TotalSeconds, 10, 12);
        Assert.Equal(TimeSpan.FromSeconds(1), f.Clock.Delays[2]);
        Assert.Contains(f.Logger.Entries, entry => entry.Contains("recipients: 4"));
        Assert.DoesNotContain(f.Logger.Entries, entry => entry.Contains("private1") || entry.Contains("private2"));
    }

    [Fact]
    public async Task ExhaustedRetriesReturnOnlyFailedOriginalBytesAndContinueSending()
    {
        var f = new SenderFixture();
        var failedBytes = SenderFixture.Message("failed", true);
        f.OnSend = (message, _) => message.Subject == "failed"
            ? Task.FromException<string>(SenderFixture.Rejection()) : Task.FromResult("accepted");
        var task = f.Sender.SendAsync([failedBytes, SenderFixture.Message("success")], default);
        await f.Clock.CompleteAsync(task);

        Assert.Same(failedBytes, Assert.Single(await task));
        Assert.Equal(4, f.Attempts.Count(attempt => attempt.Id == "failed@example.test"));
        Assert.Equal("success@example.test", f.Attempts.Last().Id);
        Assert.InRange(f.Clock.Delays[2].TotalSeconds, 30, 36);
    }

    [Theory]
    [InlineData(535)]
    [InlineData(550)]
    public async Task PermanentRejectionsAreRetainedWithoutRetries(int status)
    {
        var f = new SenderFixture();
        f.OnSend = (_, _) => Task.FromException<string>(SenderFixture.Rejection(status));
        var bytes = SenderFixture.Message();
        var failed = await f.Sender.SendAsync([bytes], default);
        Assert.Same(bytes, Assert.Single(failed));
        Assert.Single(f.Attempts);
        Assert.Empty(f.Clock.Delays);
    }

    [Fact]
    public async Task AmbiguousNetworkFailureIsRetainedWithoutAutomaticallyResending()
    {
        var f = new SenderFixture();
        f.OnSend = (_, _) => Task.FromException<string>(new IOException("Disconnected during DATA"));
        Assert.Single(await f.Sender.SendAsync([SenderFixture.Message()], default));
        Assert.Single(f.Attempts);
    }

    [Fact]
    public async Task RetryAlsoHonorsALongerConfiguredSendInterval()
    {
        var f = new SenderFixture(new EmailSettings { SendIntervalMilliseconds = 10000, RetryDelaysSeconds = [1] });
        f.OnSend = (_, _) => f.Attempts.Count == 1
            ? Task.FromException<string>(SenderFixture.Rejection()) : Task.FromResult("accepted");
        await f.Clock.CompleteAsync(f.Sender.SendAsync([SenderFixture.Message()], default));
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, f.Attempts[1].Timestamp - f.Attempts[0].Timestamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInterruptsPacingOrBackoff(bool throttle)
    {
        var f = new SenderFixture();
        using var cancellation = new CancellationTokenSource();
        if (throttle)
            f.OnSend = (_, _) => Task.FromException<string>(SenderFixture.Rejection());
        var task = f.Sender.SendAsync([SenderFixture.Message("a"), SenderFixture.Message("b")], cancellation.Token);
        await f.Clock.NextTimerAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(f.Attempts);
        Assert.Equal(cancellation.Token, f.Attempts[0].Token);
    }

    [Fact]
    public async Task ReconnectsAndAuthenticatesAfterServerClosesConnection()
    {
        var f = new SenderFixture(new EmailSettings { RequireAuthentication = true });
        var connected = false;
        var authenticated = false;
        f.Client.IsConnected.Returns(_ => connected);
        f.Client.IsAuthenticated.Returns(_ => authenticated);
        f.Client.ConnectAsync(Arg.Any<string>(), Arg.Any<int>(), SecureSocketOptions.StartTls, Arg.Any<CancellationToken>())
            .Returns(_ => { connected = true; return Task.CompletedTask; });
        f.Client.AuthenticateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { authenticated = true; return Task.CompletedTask; });
        f.OnSend = (_, _) =>
        {
            if (f.Attempts.Count != 1) return Task.FromResult("accepted");
            connected = authenticated = false;
            return Task.FromException<string>(SenderFixture.Rejection(421));
        };
        using var cancellation = new CancellationTokenSource();
        await f.Clock.CompleteAsync(f.Sender.SendAsync([SenderFixture.Message()], cancellation.Token));
        await f.Client.Received(2).ConnectAsync("localhost", 25, SecureSocketOptions.StartTls, cancellation.Token);
        await f.Client.Received(2).AuthenticateAsync("cysun@localhost.localdomain", "abcd", cancellation.Token);
    }

    [Fact]
    public async Task DisconnectFailureDoesNotTurnAcceptedMessagesIntoFailures()
    {
        var f = new SenderFixture();
        f.Client.DisconnectAsync(true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Disconnected before QUIT")));
        Assert.Empty(await f.Sender.SendAsync([SenderFixture.Message()], default));
    }

    [Fact]
    public async Task MockSendStillPacesWithoutConnectingToSmtp()
    {
        var f = new SenderFixture(new EmailSettings { MockSend = true });
        var task = f.Sender.SendAsync([SenderFixture.Message("a"), SenderFixture.Message("b")], default);
        await f.Clock.CompleteAsync(task);
        Assert.Empty(await task);
        Assert.Empty(f.Client.ReceivedCalls());
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(f.Clock.Delays));
    }
}
