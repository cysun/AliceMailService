using System.Threading.Channels;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;

namespace AliceMailService.Tests;

internal sealed class TestClock : TimeProvider
{
    private long _timestamp;
    private readonly Channel<TestTimer> _timers = Channel.CreateUnbounded<TestTimer>();
    public List<TimeSpan> Delays { get; } = [];
    public TimeSpan UtcOffset { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp()) + UtcOffset;
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, period);
        Delays.Add(dueTime);
        var timer = new TestTimer(this, callback, state, GetTimestamp() + dueTime.Ticks);
        _timers.Writer.TryWrite(timer);
        return timer;
    }

    public async Task<TestTimer> NextTimerAsync() =>
        await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    public async Task CompleteAsync(Task task)
    {
        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, _timers.Reader.WaitToReadAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            if (!task.IsCompleted && _timers.Reader.TryRead(out var timer))
                timer.Fire();
        }
        await task;
    }

    internal sealed class TestTimer(TestClock clock, TimerCallback callback, object state, long dueTimestamp) : ITimer
    {
        private volatile bool _disposed;
        public void Fire()
        {
            if (_disposed) return;
            clock.Advance(TimeSpan.FromTicks(Math.Max(0, dueTimestamp - clock.GetTimestamp())));
            callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class TestLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter) => Entries.Add(formatter(state, exception) + exception);
}

internal sealed class SenderFixture
{
    public TestClock Clock { get; } = new();
    public ISmtpClient Client { get; } = Substitute.For<ISmtpClient>();
    public TestLogger<EmailSender> Logger { get; } = new();
    public EmailSender Sender { get; }
    public List<(string Id, long Timestamp, CancellationToken Token)> Attempts { get; } = [];
    public Func<MimeMessage, CancellationToken, Task<string>> OnSend { get; set; } = (_, _) => Task.FromResult("accepted");

    public SenderFixture(EmailSettings settings = null)
    {
        Client.IsConnected.Returns(true);
        Client.IsAuthenticated.Returns(true);
        Client.SendAsync(Arg.Any<ISendRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var message = call.ArgAt<ISendRequest>(0).Message;
                var token = call.ArgAt<CancellationToken>(1);
                Attempts.Add((message.MessageId, Clock.GetTimestamp(), token));
                var response = await OnSend(message, token);
                var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).ToList();
                return new SendResult(response, recipients, []);
            });
        Sender = new EmailSender(Options.Create(settings ?? new EmailSettings()), () => Client, Clock, Logger);
    }

    public static byte[] Message(string id = "announcement", bool includeHiddenRecipients = false)
    {
        using var message = new MimeMessage();
        message.MessageId = $"{id}@example.test";
        message.Subject = id;
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse("info@example.test"));
        if (includeHiddenRecipients)
        {
            message.Cc.Add(MailboxAddress.Parse("copy@example.test"));
            var group = new GroupAddress("Hidden");
            group.Members.Add(MailboxAddress.Parse("private1@example.test"));
            group.Members.Add(MailboxAddress.Parse("private2@example.test"));
            message.Bcc.Add(group);
        }
        message.Body = new TextPart("plain") { Text = "Announcement body" };
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    public static SmtpCommandException Rejection(int status = 454) => new(
        SmtpErrorCode.MessageNotAccepted, (SmtpStatusCode)status,
        "SMTP rejection involving private1@example.test");
}
