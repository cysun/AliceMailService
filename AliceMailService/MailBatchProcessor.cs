using MessagePack;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AliceMailService;

public class MailBatchProcessor(
    EmailSender sender,
    IOptions<RabbitMQSettings> settings,
    ILogger<MailBatchProcessor> logger)
{
    public async Task RetryFailedAsync(IChannel consumerChannel, IChannel failureChannel,
        CancellationToken cancellationToken)
    {
        var queue = settings.Value.FailedQueueName;
        var batchCount = await consumerChannel.MessageCountAsync(queue, cancellationToken);
        if (batchCount == 0)
            return;

        logger.LogInformation("Retrying up to {batchCount} failed batches from {queue} before consuming new mail",
            batchCount, queue);

        // A failed retry is published back to the tail of the same queue. Limit the pass to the
        // initial ready count so those messages wait for a later restart instead of looping forever.
        uint processed = 0;
        while (processed < batchCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delivery = await consumerChannel.BasicGetAsync(queue, autoAck: false, cancellationToken);
            if (delivery is null)
                break;

            await ProcessAsync(delivery.Body, delivery.DeliveryTag, consumerChannel, failureChannel,
                cancellationToken);
            processed++;
        }

        logger.LogInformation("Startup retry pass completed for {batchCount} batches from {queue}", processed, queue);
    }

    public async Task ProcessAsync(ReadOnlyMemory<byte> body, ulong deliveryTag, IChannel consumerChannel,
        IChannel failureChannel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<byte[]> messages;
        try
        {
            messages = MessagePackSerializer.Deserialize<List<byte[]>>(body, cancellationToken: cancellationToken);
            if (messages is null || messages.Any(message => message is null))
                throw new MessagePackSerializationException("The email batch must be a list of non-null byte arrays.");
        }
        catch (MessagePackSerializationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Quarantine malformed input too, instead of leaving a poison batch stuck or dropping it.
            await RetainAsync(body, "invalid-batch", failureChannel, cancellationToken);
            await consumerChannel.BasicAckAsync(deliveryTag, false, cancellationToken);
            return;
        }

        var failed = await sender.SendAsync(messages, cancellationToken);
        if (failed.Count > 0)
        {
            var failedBody = MessagePackSerializer.Serialize(failed, cancellationToken: cancellationToken);
            await RetainAsync(failedBody, "failed-emails", failureChannel, cancellationToken);
            logger.LogWarning("Retained {failedCount} of {messageCount} emails in {queue}",
                failed.Count, messages.Count, settings.Value.FailedQueueName);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await consumerChannel.BasicAckAsync(deliveryTag, false, cancellationToken);
    }

    private async Task RetainAsync(ReadOnlyMemory<byte> body, string failureKind, IChannel failureChannel,
        CancellationToken cancellationToken)
    {
        // This channel must have publisher confirmations and confirmation tracking enabled.
        // Mandatory publishing also fails if the destination queue has been removed.
        await failureChannel.BasicPublishAsync(string.Empty, settings.Value.FailedQueueName, mandatory: true,
            basicProperties: new BasicProperties
            {
                Persistent = true,
                ContentType = "application/x-msgpack",
                Type = failureKind
            }, body: body, cancellationToken: cancellationToken);
        logger.LogWarning("Stored {failureKind} in {queue}; publisher confirmation received",
            failureKind, settings.Value.FailedQueueName);
    }
}
