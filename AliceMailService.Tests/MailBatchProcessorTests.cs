using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace AliceMailService.Tests;

public class MailBatchProcessorTests
{
    private readonly SenderFixture _sender = new(new EmailSettings { RetryDelaysSeconds = [] });
    private readonly IChannel _consumer = Substitute.For<IChannel>();
    private readonly IChannel _publisher = Substitute.For<IChannel>();
    private MailBatchProcessor Processor => new(_sender.Sender,
        Options.Create(new RabbitMQSettings { QueueName = "test-mail" }), NullLogger<MailBatchProcessor>.Instance);

    private static byte[] Batch(params byte[][] messages) => MessagePackSerializer.Serialize(messages.ToList());

    private static BasicGetResult Delivery(ulong tag, byte[] body) =>
        new(tag, false, string.Empty, "test-mail.failed", 0, new BasicProperties { Persistent = true }, body);

    [Fact]
    public async Task StartupPassSkipsAnEmptyFailedQueue()
    {
        _consumer.MessageCountAsync("test-mail.failed", default).Returns(0u);
        await Processor.RetryFailedAsync(_consumer, _publisher, default);
        await _consumer.DidNotReceiveWithAnyArgs().BasicGetAsync(default, default, default);
        Assert.Empty(_sender.Attempts);
    }

    [Fact]
    public async Task StartupPassRetriesExistingBatchesOnceAndLeavesRepeatedFailuresForNextRestart()
    {
        var failedBytes = SenderFixture.Message("failed", true);
        var queue = new Queue<BasicGetResult>([
            Delivery(10, Batch(failedBytes)),
            Delivery(11, Batch(SenderFixture.Message("success")))
        ]);
        _consumer.MessageCountAsync("test-mail.failed", default).Returns(_ => (uint)queue.Count);
        _consumer.BasicGetAsync("test-mail.failed", false, default)
            .Returns(_ => queue.TryDequeue(out var delivery) ? delivery : null);
        _publisher.BasicPublishAsync(Arg.Any<string>(), "test-mail.failed", true, Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                queue.Enqueue(Delivery(12, call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));
                return ValueTask.CompletedTask;
            });
        _sender.OnSend = (message, _) => message.Subject == "failed"
            ? Task.FromException<string>(SenderFixture.Rejection(550)) : Task.FromResult("accepted");

        await _sender.Clock.CompleteAsync(Processor.RetryFailedAsync(_consumer, _publisher, default));

        Assert.Equal(new[] { "failed@example.test", "success@example.test" }, _sender.Attempts.Select(a => a.Id));
        var remaining = MessagePackSerializer.Deserialize<List<byte[]>>(Assert.Single(queue).Body);
        Assert.Equal(failedBytes, Assert.Single(remaining));
        await _consumer.Received(1).BasicAckAsync(10, false, default);
        await _consumer.Received(1).BasicAckAsync(11, false, default);
        await _consumer.Received(2).BasicGetAsync("test-mail.failed", false, default);

        // The next startup gets another chance to deliver the retained failure.
        _sender.OnSend = (_, _) => Task.FromResult("accepted");
        await _sender.Clock.CompleteAsync(Processor.RetryFailedAsync(_consumer, _publisher, default));
        Assert.Empty(queue);
        Assert.Equal(3, _sender.Attempts.Count);
        await _consumer.Received(1).BasicAckAsync(12, false, default);
    }

    [Fact]
    public async Task StartupReplayUsesBackoffAndSharesPacingWithNormalDelivery()
    {
        var sender = new SenderFixture();
        var processor = new MailBatchProcessor(sender.Sender,
            Options.Create(new RabbitMQSettings { QueueName = "test-mail" }), NullLogger<MailBatchProcessor>.Instance);
        _consumer.MessageCountAsync("test-mail.failed", default).Returns(1u);
        _consumer.BasicGetAsync("test-mail.failed", false, default)
            .Returns(Delivery(10, Batch(SenderFixture.Message("retry"))));
        sender.OnSend = (_, _) => sender.Attempts.Count == 1
            ? Task.FromException<string>(SenderFixture.Rejection()) : Task.FromResult("accepted");

        await sender.Clock.CompleteAsync(processor.RetryFailedAsync(_consumer, _publisher, default));
        await sender.Clock.CompleteAsync(processor.ProcessAsync(
            Batch(SenderFixture.Message("new")), 11, _consumer, _publisher, default));

        Assert.Equal(new[] { "retry@example.test", "retry@example.test", "new@example.test" },
            sender.Attempts.Select(a => a.Id));
        Assert.InRange(sender.Clock.Delays[0].TotalSeconds, 5, 6);
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, sender.Attempts[2].Timestamp - sender.Attempts[1].Timestamp);
        Assert.Empty(_publisher.ReceivedCalls());
        await _consumer.Received(1).BasicAckAsync(10, false, default);
        await _consumer.Received(1).BasicAckAsync(11, false, default);
    }

    [Fact]
    public async Task StartupReplayStopsIfFailedQueueBecomesEmpty()
    {
        _consumer.MessageCountAsync("test-mail.failed", default).Returns(3u);
        _consumer.BasicGetAsync("test-mail.failed", false, default).Returns((BasicGetResult)null);
        await Processor.RetryFailedAsync(_consumer, _publisher, default);
        await _consumer.Received(1).BasicGetAsync("test-mail.failed", false, default);
        Assert.Empty(_sender.Attempts);
    }

    [Fact]
    public async Task StartupReplayLeavesDeliveryUnacknowledgedIfRetentionFails()
    {
        _consumer.MessageCountAsync("test-mail.failed", default).Returns(1u);
        _consumer.BasicGetAsync("test-mail.failed", false, default)
            .Returns(Delivery(10, Batch(SenderFixture.Message())));
        _sender.OnSend = (_, _) => Task.FromException<string>(SenderFixture.Rejection(550));
        _publisher.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new IOException("Publisher confirmation failed")));

        await Assert.ThrowsAsync<IOException>(() => Processor.RetryFailedAsync(_consumer, _publisher, default));
        await _consumer.DidNotReceiveWithAnyArgs().BasicAckAsync(default, default, default);
    }

    [Fact]
    public async Task ShutdownDuringStartupBackoffLeavesBatchUnacknowledgedAndStopsFetching()
    {
        var sender = new SenderFixture();
        var processor = new MailBatchProcessor(sender.Sender,
            Options.Create(new RabbitMQSettings { QueueName = "test-mail" }), NullLogger<MailBatchProcessor>.Instance);
        using var cancellation = new CancellationTokenSource();
        _consumer.MessageCountAsync("test-mail.failed", cancellation.Token).Returns(2u);
        _consumer.BasicGetAsync("test-mail.failed", false, cancellation.Token)
            .Returns(Delivery(10, Batch(SenderFixture.Message())));
        sender.OnSend = (_, _) => Task.FromException<string>(SenderFixture.Rejection());

        var replay = processor.RetryFailedAsync(_consumer, _publisher, cancellation.Token);
        await sender.Clock.NextTimerAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.WaitAsync(TimeSpan.FromSeconds(5)));
        await _consumer.Received(1).BasicGetAsync("test-mail.failed", false, cancellation.Token);
        await _consumer.DidNotReceiveWithAnyArgs().BasicAckAsync(default, default, default);
        Assert.Empty(_publisher.ReceivedCalls());
    }

    [Fact]
    public async Task SuccessfulBatchIsAcknowledgedWithoutPublishingFailures()
    {
        await _sender.Clock.CompleteAsync(Processor.ProcessAsync(
            Batch(SenderFixture.Message("a"), SenderFixture.Message("b")), 42, _consumer, _publisher, default));
        await _consumer.Received(1).BasicAckAsync(42, false, default);
        Assert.Empty(_publisher.ReceivedCalls());
    }

    [Fact]
    public async Task PublishesOnlyFailedOriginalMessagesWithPersistentMandatoryDelivery()
    {
        var failedBytes = SenderFixture.Message("failed", true);
        _sender.OnSend = (message, _) => message.Subject == "failed"
            ? Task.FromException<string>(SenderFixture.Rejection(550)) : Task.FromResult("accepted");
        await _sender.Clock.CompleteAsync(Processor.ProcessAsync(
            Batch(SenderFixture.Message("success"), failedBytes), 42, _consumer, _publisher, default));

        var call = Assert.Single(_publisher.ReceivedCalls());
        var args = call.GetArguments();
        Assert.Equal(string.Empty, args[0]);
        Assert.Equal("test-mail.failed", args[1]);
        Assert.Equal(true, args[2]);
        var properties = Assert.IsType<BasicProperties>(args[3]);
        Assert.True(properties.Persistent);
        Assert.Equal("failed-emails", properties.Type);
        var payload = Assert.IsType<ReadOnlyMemory<byte>>(args[4]);
        var failed = MessagePackSerializer.Deserialize<List<byte[]>>(payload);
        Assert.Equal(failedBytes, Assert.Single(failed));
        await _consumer.Received(1).BasicAckAsync(42, false, default);
    }

    [Fact]
    public async Task WaitsForFailurePublishConfirmationBeforeAcknowledging()
    {
        _sender.OnSend = (_, _) => Task.FromException<string>(SenderFixture.Rejection(550));
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _publisher.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { published.TrySetResult(); return new ValueTask(confirmation.Task); });

        var processing = Processor.ProcessAsync(Batch(SenderFixture.Message()), 42, _consumer, _publisher, default);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(processing.IsCompleted);
        await _consumer.DidNotReceiveWithAnyArgs().BasicAckAsync(default, default, default);

        confirmation.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        await _consumer.Received(1).BasicAckAsync(42, false, default);
    }

    [Fact]
    public async Task FailedPublishLeavesOriginalBatchUnacknowledged()
    {
        _sender.OnSend = (_, _) => Task.FromException<string>(SenderFixture.Rejection(550));
        _publisher.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new IOException("Publisher confirmation failed")));

        await Assert.ThrowsAsync<IOException>(() => Processor.ProcessAsync(
            Batch(SenderFixture.Message()), 42, _consumer, _publisher, default));
        await _consumer.DidNotReceiveWithAnyArgs().BasicAckAsync(default, default, default);
        Assert.Single(_sender.Attempts);
    }

    [Fact]
    public async Task MalformedBatchIsPreservedBeforeAcknowledgmentWithoutSending()
    {
        var body = MessagePackSerializer.Serialize("not a batch");
        await Processor.ProcessAsync(body, 42, _consumer, _publisher, default);
        var args = Assert.Single(_publisher.ReceivedCalls()).GetArguments();
        Assert.Equal(body, Assert.IsType<ReadOnlyMemory<byte>>(args[4]).ToArray());
        Assert.Equal("invalid-batch", Assert.IsType<BasicProperties>(args[3]).Type);
        Assert.Empty(_sender.Attempts);
        await _consumer.Received(1).BasicAckAsync(42, false, default);
    }

    [Fact]
    public async Task ShutdownDuringBackoffDoesNotAcknowledgeOrPublishAsFailed()
    {
        var sender = new SenderFixture();
        sender.OnSend = (_, _) => Task.FromException<string>(SenderFixture.Rejection());
        var processor = new MailBatchProcessor(sender.Sender, Options.Create(new RabbitMQSettings()),
            NullLogger<MailBatchProcessor>.Instance);
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(Batch(SenderFixture.Message()), 42, _consumer, _publisher, cancellation.Token);
        await sender.Clock.NextTimerAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        await _consumer.DidNotReceiveWithAnyArgs().BasicAckAsync(default, default, default);
        Assert.Empty(_publisher.ReceivedCalls());
    }
}
