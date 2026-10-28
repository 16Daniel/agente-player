using DigitalSignage.Agent.Configuration;
using Microsoft.Extensions.Options;


namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Supervisa periódicamente la disponibilidad de la API central.
    /// Detecta pérdidas de comunicación y recuperaciones, evitando
    /// registrar repetidamente el mismo estado.
    /// </summary>
    public sealed class ApiMonitoringService(ILogger<ApiMonitoringService> logger, ApiCommunicationService apiCommunicationService, IOptions<AgentOptions> agentOptions, PlayerInformationService playerInformationService) : BackgroundService
    {
        /// <summary>
        /// Ejecuta la supervisión periódica de la API hasta que
        /// se solicita la detención del agente.
        /// </summary>
        /// <param name="stoppingToken">
        /// Token de cancelación asociado al ciclo de vida del servicio.
        /// </param>
        /// <returns>Tarea que representa la ejecución del servicio.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Intervalo de tiempo entre cada verificación de disponibilidad de la API.
            TimeSpan checkInterval = TimeSpan.FromSeconds(
                agentOptions.Value.Api.CheckIntervalSeconds);

            // null: sin verificación previa; true: disponible; false: no disponible.
            bool? lastAvailable = null;

            logger.LogInformation(
                "Supervisión de API iniciada. Intervalo: {Seconds} segundos.",
                checkInterval.TotalSeconds);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        // La solicitud conserva la validación HTTPS configurada.
                        await apiCommunicationService.GetAsync(
                            "Agent/Health",
                            stoppingToken);

                        if (lastAvailable != true)
                        {
                            logger.LogInformation(
                                lastAvailable == false
                                    ? "Comunicación con la API central recuperada."
                                    : "Comunicación inicial con la API central establecida.");
                        }

                        lastAvailable = true;

                        // Reportar información del dispositivo a la plataforma web central.
                        try
                        {
                            // Ejecucion de SP: SpOpMxDS_ActualizarInformacionPlayer que actualizara la informacion obtenida del player en SQL Server.
                            await playerInformationService.ReportarInformacionAsync(stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(
                                ex,
                                "No fue posible reportar la información del Player a la API central.");
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (lastAvailable != false)
                        {
                            logger.LogWarning(
                                ex,
                                "API central no disponible. El agente continuará funcionando.");
                        }

                        lastAvailable = false;
                    }

                    await Task.Delay(checkInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Detención normal del servicio.
            }
            finally
            {
                logger.LogInformation("Supervisión de API finalizada.");
            }
        }

    }
}
