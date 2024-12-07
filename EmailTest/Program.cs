using AliceMailService;
using MessagePack;
using Microsoft.Extensions.Configuration;
using MimeKit;
using RabbitMQ.Client;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var mqSettings = config.GetSection("RabbitMQ").Get<RabbitMQSettings>();
var factory = new ConnectionFactory
{
    HostName = mqSettings.HostName,
    UserName = mqSettings.UserName,
    Password = mqSettings.Password
};
await using var connection = await factory.CreateConnectionAsync();
await using var channel = await connection.CreateChannelAsync();
await channel.QueueDeclareAsync(mqSettings.QueueName, true, false, false);

Console.Write("From: ");
var from = Console.ReadLine();
Console.Write("To: ");
var to = Console.ReadLine();
Console.Write("Subject: ");
var subject = Console.ReadLine();
Console.Write("Content: ");
var content = Console.ReadLine();

var msg = new MimeMessage();
msg.From.Add(new MailboxAddress("Test Sender", from));
msg.To.Add(new MailboxAddress("Test Recipient", to));
msg.Subject = subject;
msg.Body = new TextPart("html")
{
    Text = $"<p>{content}</p>"
};

var messages = new List<byte[]>();
using (var stream = new MemoryStream())
{
    msg.WriteTo(stream);
    messages.Add(stream.ToArray());
}

var body = MessagePackSerializer.Serialize(messages);

await channel.BasicPublishAsync(string.Empty, mqSettings.QueueName, body);

Console.WriteLine("Message sent!");