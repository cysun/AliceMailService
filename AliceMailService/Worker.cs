using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MessagePack;
using Microsoft.Extensions.Options;
using MimeKit;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AliceMailService;

public class RabbitMQSettings
{
    public string HostName { get; init; } = "localhost";
    public string UserName { get; init; } = ConnectionFactory.DefaultUser;
    public string Password { get; init; } = ConnectionFactory.DefaultPass;
    public string QueueName { get; init; } = "alice-mail-service";
}

public class EmailSettings
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 25;
    public bool RequireAuthentication { get; init; }
    public string Username { get; init; } = "cysun@localhost.localdomain";
    public string Password { get; init; } = "abcd";
    public string AlertSender { get; init; }
    public string AlertRecipient { get; init; }
    public bool MockSend { get; init; } // for testing in environments without a local email server
}

public class Worker(
    IOptions<RabbitMQSettings> mqSettings,
    IOptions<EmailSettings> emailSettings,
    ILogger<Worker> logger)
    : BackgroundService
{
    private readonly EmailSettings _emailSettings = emailSettings.Value;
    private readonly RabbitMQSettings _mqSettings = mqSettings.Value;
    private IChannel _channel;
    private IConnection _connection;
    private AsyncEventingBasicConsumer _consumer;

    private ConnectionFactory _factory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _factory = new ConnectionFactory
        {
            HostName = _mqSettings.HostName,
            UserName = _mqSettings.UserName,
            Password = _mqSettings.Password
        };

        try
        {
            _connection = await _factory.CreateConnectionAsync(stoppingToken);
            logger.LogInformation("Connected to RabbitMQ server at {host}", _mqSettings.HostName);

            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await _channel.QueueDeclareAsync(_mqSettings.QueueName, true, false, false,
                cancellationToken: stoppingToken);
            logger.LogInformation("Queue {queue} declared", _mqSettings.QueueName);

            _consumer = new AsyncEventingBasicConsumer(_channel);
            _consumer.ReceivedAsync += async (_, ea) =>
            {
                var messages = MessagePackSerializer.Deserialize<List<byte[]>>(ea.Body.ToArray())
                    .Select(bytes =>
                    {
                        using var stream = new MemoryStream(bytes);
                        return MimeMessage.Load(stream);
                    }).ToList();
                await SendEmailAsync(messages);
                await _channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
            };
            await _channel.BasicConsumeAsync(_mqSettings.QueueName, false, _consumer, stoppingToken);
            logger.LogInformation("Consumer listens to queue {queue}", _mqSettings.QueueName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to RabbitMQ server at {host}", _mqSettings.HostName);
            SendAlert("Failed to Connect to RabbitMQ Server", ex.Message);
        }
    }

    private async Task SendEmailAsync(List<MimeMessage> messages)
    {
        if (_emailSettings.MockSend)
        {
            MockSendEmail(messages);
            return;
        }

        using var client = new SmtpClient();

        try
        {
            if (_emailSettings.RequireAuthentication)
            {
                await client.ConnectAsync(_emailSettings.Host, _emailSettings.Port, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_emailSettings.Username, _emailSettings.Password);
            }
            else // for testing with local SMTP server
            {
                await client.ConnectAsync(_emailSettings.Host, _emailSettings.Port, SecureSocketOptions.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to SMTP server");
        }

        foreach (var message in messages)
            try
            {
                await client.SendAsync(message);
                logger.LogInformation("Message [{subject}] sent to {recipient}", message.Subject, message.To);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send message [{subject}] sent to {recipient}", message.Subject,
                    message.To);
            }

        await client.DisconnectAsync(true);
    }

    private void MockSendEmail(List<MimeMessage> messages)
    {
        foreach (var message in messages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("***");
            sb.AppendLine($"From: {message.From}");
            sb.AppendLine($"To: {message.To}");
            sb.AppendLine($"Subject: {message.Subject}");
            sb.AppendLine($"{message.Body}");
            sb.AppendLine("***");
            logger.LogInformation(sb.ToString());
        }
    }

    // This is basically a synchronous version of SendEmail. There should be no need for this method since we should
    // be able to use SendMailAsync, but the problem is that C# has some weird rules about await an async method in a
    // catch block, which is where I want to send an alert email. Technically we can await an async method in a catch
    // block since C# 6, but it would require changing the return type of the method from Task to "async Task<Task>".
    // I've yet to fully understood this, so the current workaround is to use this synchronous method.
    private void SendAlert(string subject, string content)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_emailSettings.AlertSender));
        message.To.Add(MailboxAddress.Parse(_emailSettings.AlertRecipient));
        message.Subject = subject;
        message.Body = new TextPart("plain")
        {
            Text = content
        };

        if (_emailSettings.MockSend)
        {
            MockSendEmail([message]);
            return;
        }

        using var client = new SmtpClient();

        try
        {
            if (_emailSettings.RequireAuthentication)
            {
                client.Connect(_emailSettings.Host, _emailSettings.Port, SecureSocketOptions.StartTls);
                client.Authenticate(_emailSettings.Username, _emailSettings.Password);
            }
            else // for testing with local SMTP server
            {
                client.Connect(_emailSettings.Host, _emailSettings.Port, SecureSocketOptions.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to SMTP server");
        }

        try
        {
            client.SendAsync(message);
            logger.LogInformation("Message [{subject}] sent to {recipient}", message.Subject, message.To);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message [{subject}] sent to {recipient}", message.Subject,
                message.To);
        }

        client.Disconnect(true);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _channel.CloseAsync(cancellationToken);
        await _connection.CloseAsync(cancellationToken);
        logger.LogInformation("Channel and connection closed");

        SendAlert("AliceMailService Stopped", "The service has been stopped.");

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
        logger.LogInformation("Channel and connection disposed");

        base.Dispose();
    }
}