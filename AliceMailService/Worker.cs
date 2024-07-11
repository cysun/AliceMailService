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
}

public class Worker : BackgroundService
{
    private readonly RabbitMQSettings _mqSettings;
    private readonly EmailSettings _emailSettings;

    private readonly ConnectionFactory _factory;

    private readonly ILogger<Worker> _logger;

    public Worker(IOptions<RabbitMQSettings> mqSettings, IOptions<EmailSettings> emailSettings, ILogger<Worker> logger)
    {
        _mqSettings = mqSettings.Value;
        _emailSettings = emailSettings.Value;
        _logger = logger;

        _factory = new ConnectionFactory
        {
            HostName = _mqSettings.HostName,
            UserName = _mqSettings.UserName,
            Password = _mqSettings.Password
        };
        using var connection = _factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.QueueDeclare(queue: _mqSettings.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var connection = _factory.CreateConnection();
            using var channel = connection.CreateModel();
            channel.QueueDeclarePassive(_mqSettings.QueueName);

            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += async (model, ea) =>
            {
                var messages = MessagePackSerializer.Deserialize<List<byte[]>>(ea.Body.ToArray())
                .Select(bytes =>
                {
                    using var stream = new MemoryStream(bytes);
                    return MimeMessage.Load(stream);
                }).ToList();
                await SendEmailAsync(messages);
            };
        }

        return Task.CompletedTask;
    }

    private async Task SendEmailAsync(List<MimeMessage> messages)
    {
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
}
