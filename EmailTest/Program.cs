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
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();
await channel.QueueDeclareAsync(mqSettings.QueueName, true, false, false, null);

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
msg.To.Add(new MailboxAddress("Test Receipient", to));
msg.Subject = subject;
msg.Body = new TextPart("html")
{
    Text = $"<p>{content}</p>"
};

List<byte[]> messages = new List<byte[]>();
using (MemoryStream stream = new MemoryStream())
{
    msg.WriteTo(stream);
    messages.Add(stream.ToArray());
}

var body = MessagePackSerializer.Serialize(messages);

await channel.BasicPublishAsync(exchange: string.Empty, routingKey: mqSettings.QueueName, body: body);

Console.WriteLine("Message sent!");
