
using DigitalSignage.Agent;
using DigitalSignage.Agent.Configuration;
using DigitalSignage.Agent.Services;
using DigitalSignage.Agent.Logging;
using Microsoft.Extensions.Logging.Console;
using Serilog;
using Serilog.Events;
using Serilog.Debugging;

var builder = Host.CreateApplicationBuilder(args);

// ============================================================================================
// Registrar la configuración general del agente.
// Los valores se obtienen de la sección "Agent" de appsettings.json.
builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .Validate(
        options => options.Api.TimeoutSeconds > 0,
        "Agent:Api:TimeoutSeconds debe ser mayor que cero.")
    .Validate(
        options => options.Network.CheckIntervalSeconds > 0,
        "Agent:Network:CheckIntervalSeconds debe ser mayor que cero.")
    .Validate(
        options => string.IsNullOrWhiteSpace(options.Api.BaseUrl) ||
                   (Uri.TryCreate(options.Api.BaseUrl, UriKind.Absolute, out Uri? uri) &&
                    uri.Scheme == Uri.UriSchemeHttps),
        "Agent:Api:BaseUrl debe contener una URL HTTPS válida.")
    .Validate(
        options => options.Api.CheckIntervalSeconds > 0,
        "Agent:Api:CheckIntervalSeconds debe ser mayor que cero.")
    .Validate(
        options => options.Commands.CheckIntervalSeconds > 0,
        "Agent:commands:CheckIntervalSeconds El intervalo de consulta de comandos debe ser mayor a 0 segundos.")
    .ValidateOnStart();

// ============================================================================================
// Registrar los servicios utilizados para saber información del player.
builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection("Agent:Player:Informacion"));

// ============================================================================================
// Registrar los servicios utilizados por el agente.
builder.Services.AddSingleton<HardwareService>(); // Servicio para obtener información del hardware.
builder.Services.AddSingleton<NetworkService>(); // Servicio para obtener información de red.


// Registrar la infraestructura para crear y administrar clientes HTTP.
builder.Services.AddHttpClient();
// Registrar el servicio de comunicación con la API.
builder.Services.AddSingleton<ApiCommunicationService>();

// Registrar el servicio de supervisión de la API.
builder.Services.AddHostedService<ApiMonitoringService>();

// Registrar el servicio independiente de consulta de comandos del Player.
builder.Services.AddHostedService<PlayerCommandService>();

// Servicio compartido para reportar información del Player.
builder.Services.AddSingleton<PlayerInformationService>();

// ============================================================================================
// Registrar el servicio principal de ejecución.
builder.Services.AddHostedService<Worker>();

// ============================================================================================
// Configurar el formato personalizado de los registros de consola.
builder.Logging.AddConsole(options =>
{
    options.FormatterName = "AgentConsole";
});

builder.Logging.AddConsoleFormatter<AgentConsoleFormatter, ConsoleFormatterOptions>();


// ============================================================================================
// Configurar el almacenamiento persistente de registros.
//
// Serilog escribe los eventos en archivos locales mediante un proceso
// asíncrono, evitando bloquear directamente los servicios del agente.
//
// El proveedor de consola existente se conserva para mostrar los
// mensajes en tiempo real con AgentConsoleFormatter.

const string logFilePath = "/var/log/digital-signage/agent-.log";

// Habilitar diagnósticos internos de Serilog durante las pruebas.
// Permite identificar errores al abrir o escribir archivos de registro.
SelfLog.Enable(message => Console.Error.WriteLine(
    $"[SERILOG] {message}"));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.Async(writeTo => writeTo.File(
        path: logFilePath,
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 14,
        shared: false))
    .CreateLogger();

// Registrar Serilog como proveedor adicional, conservando
// el proveedor de consola personalizado.
builder.Logging.AddSerilog(Log.Logger, dispose: true);


// ============================================================================================
// Construir e iniciar el agente.
var host = builder.Build();
host.Run();