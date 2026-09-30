using AliceMailService;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var services = builder.Services;

services.AddSystemd();
services.AddSerilog((serviceProvider, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(serviceProvider)
);

services.AddOptions<RabbitMQSettings>()
    .Bind(builder.Configuration.GetSection("RabbitMQ"))
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.QueueName), "RabbitMQ:QueueName is required.")
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<EmailSettings>, EmailSettingsValidator>();
services.AddOptions<EmailSettings>()
    .Bind(builder.Configuration.GetSection("Email"))
    .ValidateOnStart();
services.AddSingleton(TimeProvider.System);
services.AddSingleton<Func<ISmtpClient>>(_ => () => new SmtpClient());
services.AddSingleton<EmailSender>();
services.AddSingleton<MailBatchProcessor>();
services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
