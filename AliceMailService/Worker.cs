using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AliceMailService;

public class RabbitMQSettings
{
    public string HostName { get; init; } = "localhost";
    public string UserName { get; init; } = ConnectionFactory.DefaultUser;
    public string Password { get; init; } = ConnectionFactory.DefaultPass;
    public string QueueName { get; init; } = "alice-mail-service";
    public string FailedQueueName => $"{QueueName}.failed";
}

public class Worker(
    IOptions<RabbitMQSettings> mqSettings,
    MailBatchProcessor processor,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = mqSettings.Value;
        var factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            UserName = settings.UserName,
            Password = settings.Password,
            ConsumerDispatchConcurrency = 1
        };

        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            await using var connection = await factory.CreateConnectionAsync(stoppingToken);
            logger.LogInformation("Connected to RabbitMQ server at {host}", settings.HostName);

            await using var consumerChannel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            // Keep confirmations on a separate channel from the consumer callback.
            await using var failureChannel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true), stoppingToken);

            await consumerChannel.QueueDeclareAsync(settings.QueueName, true, false, false,
                cancellationToken: stoppingToken);
            await failureChannel.QueueDeclareAsync(settings.FailedQueueName, true, false, false,
                cancellationToken: stoppingToken);
            await consumerChannel.BasicQosAsync(0, 1, false, stoppingToken);

            await processor.RetryFailedAsync(consumerChannel, failureChannel, stoppingToken);

            var processingFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var consumer = new AsyncEventingBasicConsumer(consumerChannel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    await processor.ProcessAsync(ea.Body, ea.DeliveryTag, consumerChannel, failureChannel,
                        processingCancellation.Token);
                }
                catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
                {
                    // Leave the batch unacknowledged. Closing the connection makes it available again.
                }
                catch (Exception ex)
                {
                    // Consumer callback exceptions otherwise leave the host running with a stuck batch.
                    // Stop instead of acknowledging or immediately replaying a partially delivered batch.
                    processingCancellation.Cancel();
                    processingFailure.TrySetResult(ex);
                }
            };

            await consumerChannel.BasicConsumeAsync(settings.QueueName, false, consumer, stoppingToken);
            logger.LogInformation("Consumer listens to {queue}; failed emails are retained in {failedQueue}",
                settings.QueueName, settings.FailedQueueName);

            var shutdown = Task.Delay(Timeout.Infinite, stoppingToken);
            if (await Task.WhenAny(shutdown, processingFailure.Task) == processingFailure.Task)
                throw new InvalidOperationException(
                    "Failed to process or retain an email batch. The original batch was not acknowledged.",
                    await processingFailure.Task);
            await shutdown;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Mail worker stopped; RabbitMQ channels and connection disposed");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Mail worker stopped after a RabbitMQ or batch processing failure");
            throw;
        }
    }
}
