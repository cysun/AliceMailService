using AliceMailService;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var services = builder.Services;

services.AddSystemd();
services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
);

services.Configure<RabbitMQSettings>(builder.Configuration.GetSection("RabbitMQ"));
services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
