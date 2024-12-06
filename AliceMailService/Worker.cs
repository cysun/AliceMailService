using MailKit.Net.Smtp;
using MailKit.Security;
using MessagePack;
using Microsoft.Extensions.Options;
using MimeKit;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;

namespace AliceMailService;

public class RabbitMQSettings
{
    public string HostName { get; set; } = "localhost";
    public string UserName { get; set; } = ConnectionFactory.DefaultUser;
    public string Password { get; set; } = ConnectionFactory.DefaultPass;
    public string QueueName { get; set; } = "alice-mail-service";
}

public class EmailSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25;
    public bool RequireAuthentication { get; set; } = false;
    public string Username { get; set; } = "cysun@localhost.localdomain";
    public string Password { get; set; } = "abcd";
    public string AlertSender { get; set; }
    public string AlertRecipient { get; set; }
    public bool MockSend { get; set; } = false;
}

public class Worker : BackgroundService
{
    private readonly RabbitMQSettings _mqSettings;
    private readonly EmailSettings _emailSettings;

    private ConnectionFactory _factory;
    private IConnection _connection;
    private IChannel _channel;
    private AsyncEventingBasicConsumer _consumer;

    private readonly ILogger<Worker> _logger;

    public Worker(IOptions<RabbitMQSettings> mqSettings, IOptions<EmailSettings> emailSettings, ILogger<Worker> logger)
    {
        _mqSettings = mqSettings.Value;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

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
            _connection = await _factory.CreateConnectionAsync();
            _logger.LogInformation("Connected to RabbitMQ server at {host}", _mqSettings.HostName);

            _channel = await _connection.CreateChannelAsync();
            await _channel.QueueDeclareAsync(queue: _mqSettings.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);
            _logger.LogInformation("Queue {queue} declared", _mqSettings.QueueName);

            _consumer = new AsyncEventingBasicConsumer(_channel);
            _consumer.ReceivedAsync += async (model, ea) =>
            {
                var messages = MessagePackSerializer.Deserialize<List<byte[]>>(ea.Body.ToArray())
                    .Select(bytes =>
                    {
                        using var stream = new MemoryStream(bytes);
                        return MimeMessage.Load(stream);
                    }).ToList();
                await SendEmailAsync(messages);
                await _channel.BasicAckAsync(deliveryTag: ea.DeliveryTag, multiple: false);
            };
            await _channel.BasicConsumeAsync(queue: _mqSettings.QueueName, autoAck: false, consumer: _consumer);
            _logger.LogInformation("Consumer listens to queue {queue}", _mqSettings.QueueName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ server at {host}", _mqSettings.HostName);
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
            _logger.LogError(ex, "Failed to connect to SMTP server");
        }

        foreach (var message in messages)
        {
            try
            {
                await client.SendAsync(message);
                _logger.LogInformation("Message [{subject}] sent to {receipient}", message.Subject, message.To);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message [{subject}] sent to {receipient}", message.Subject, message.To);
            }
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
            _logger.LogInformation(sb.ToString());
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
            MockSendEmail(new List<MimeMessage> { message });
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
            _logger.LogError(ex, "Failed to connect to SMTP server");
        }

        try
        {
            client.SendAsync(message);
            _logger.LogInformation("Message [{subject}] sent to {receipient}", message.Subject, message.To);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message [{subject}] sent to {receipient}", message.Subject, message.To);
        }

        client.Disconnect(true);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _channel.CloseAsync();
        await _connection.CloseAsync();
        _logger.LogInformation("Channel and connection closed");

        SendAlert("AliceMailService Stopped", "The service has been stopped.");

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
        _logger.LogInformation("Channel and connection disposed");

        base.Dispose();
    }
}
