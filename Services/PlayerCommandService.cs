using DigitalSignage.Agent.Configuration;
using DigitalSignage.Agent.Models;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Supervisa los comandos pendientes asignados al Player.
    /// Por ahora, únicamente los consulta y registra.
    /// </summary>
    public sealed class PlayerCommandService(ApiCommunicationService apiCommunicationService, PlayerInformationService playerInformationService, IOptions<AgentOptions> agentOptions, ILogger<PlayerCommandService> logger) : BackgroundService
    {
        /// <summary>
        /// Consulta periódicamente los comandos pendientes que debe ejecutar el player mientras
        /// el agente permanezca en ejecución.
        /// </summary>
        /// <param name="stoppingToken">
        /// Token que permite detener el servicio de forma controlada.
        /// </param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Servicio de comandos del Player iniciado.");

            // Se consultaran cada cierta caidad de segundos para saber si hay comandos pendientes.
            // el tiempo en segundos se obtiene de la configuracion del agente en el archivo appsettings.json
            int intervaloSegundos = agentOptions.Value.Commands.CheckIntervalSeconds;

            logger.LogInformation(
                "Intervalo de consulta de comandos: {Intervalo} segundos.",
                intervaloSegundos);

            using PeriodicTimer timer = new(TimeSpan.FromSeconds(intervaloSegundos));

            try
            {
                do
                {
                    try
                    {
                        // Consultar los comandos pendientes del Player. Se dirige al metodo de la API que devuelve los comandos pendientes para el Player.
                        // Consulta el SP: SpOpMxDS_ObtenerComandosPendientesPlayer en la web API, que devuelve los comandos pendientes para el Player.
                        List<PlayerComandoPendiente> comandos = await apiCommunicationService.ObtenerComandosPendientesAsync(stoppingToken);

                        // Cuando el Agent detecte un comando REPORTAR_INFO:
                        // 1.Verificará que reconoce el tipo de comando.
                        // 2.Enviará la petición a IniciarComandoPlayer.
                        // 3.SQL Server registrará su recepción y cambiará el estado a En_proceso.
                        // 4.El Agent registrará el resultado en sus logs.
                        foreach (PlayerComandoPendiente comando in comandos)
                        {
                            if (!string.Equals(comando.codigo_tipo_comando, "REPORTAR_INFO", StringComparison.OrdinalIgnoreCase))
                            {
                                logger.LogWarning("Tipo de comando no reconocido. Id: {IdComando}, Tipo: {TipoComando}.", comando.id_comando_player, 
                                    comando.codigo_tipo_comando);
                                continue;
                            }

                            // Se declara que el comando aun no a iniciado y continua en estatus de "Pendiente"
                            bool comandoIniciado = false;
                            // Se declara variable para saber si el reporte fue exitoso o no, y se inicializa en falso.
                            bool reporteExitoso = false;
                            // Se declara variable para almacenar el mensaje de resultado del reporte.
                            string mensajeResultado = string.Empty;
                            
                            try
                            {
                                logger.LogInformation("Iniciando comando REPORTAR_INFO. Id: {IdComando}.", comando.id_comando_player);

                                // Ejecucion del SP: SpOpMxDS_IniciarComandoPlayer en la web API, que actualiza el estatus del comando a ejecutar por el player
                                // pasa de Pendiente -> En_proceso.
                                await apiCommunicationService.IniciarComandoPlayerAsync(comando.id_comando_player, stoppingToken);

                                // El estatus del comando cambia de Pendiente -> En_proceso, y se registra la fecha y hora de inicio.
                                comandoIniciado = true;

                                // 2. Reportar inmediatamente la información del Player.
                                // Ejecuta el reporte de información.
                                // Este bloque captura exclusivamente los errores del reporte para la informacion del player.
                                try
                                {
                                    // Ejecutar inmediatamente el reporte solicitado. ira a la clase PlayerInformationService.ReportarInformacionAsync()
                                    // que se encarga de recopilar y enviar la información actual del Player a la API central y registrar la data en SQL.
                                    await playerInformationService.ReportarInformacionAsync(stoppingToken);

                                    reporteExitoso = true;
                                    mensajeResultado = "Información del Player reportada correctamente.";
                                }
                                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    // Si falla el reporte, se registra el error real para
                                    // finalizar posteriormente el comando con estado "Error".
                                    mensajeResultado = ex.Message;

                                    logger.LogError(ex, "Falló el reporte de información del comando {IdComando}.", comando.id_comando_player);
                                }

                                // 3. Registrar el resultado del comando.
                                //
                                // PROTECCIÓN:
                                // El resultado se determina exclusivamente por el éxito o error
                                // del "reporte de información", no por la respuesta de esta petición.
                                //
                                // Si la API registra "Ccompletado" en SQL Server, pero se pierde
                                // la respuesta HTTP, esta llamada generará una excepción.
                                //
                                // Dicha excepción será capturada por el catch general,
                                // que únicamente registrará el problema en el log.
                                // NO se realizará un segundo intento para marcarlo como "Error".
                                //
                                // Esto evita sobrescribir incorrectamente un comando que
                                // realmente se completó de forma exitosa.

                                // Confirmar que el reporte se realizó correctamente.
                                // Ejecucion del SP: SpOpMxDS_FinalizarComandoPlayer en la web API, que actualiza el estatus del comando a ejecutar por el player
                                // pasa de En_proceso -> Completado, y se registra la fecha y hora de finalización.
                                await apiCommunicationService.FinalizarComandoPlayerAsync(
                                    comando.id_comando_player,
                                    reporteExitoso ? "Completado" : "Error",
                                    mensajeResultado,
                                    stoppingToken);
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                // PROTECCIÓN:
                                // Este bloque solo registra el error.
                                // No llama nuevamente a FinalizarComandoPlayerAsync().
                                //
                                // Así evitamos enviar ERROR cuando el reporte fue exitoso,
                                // pero falló la confirmación HTTP de su finalización.
                                logger.LogError(
                                    ex,
                                    "No fue posible completar el procesamiento del comando {IdComando}. " +
                                    "Iniciado: {ComandoIniciado}. Reporte exitoso: {ReporteExitoso}.",
                                    comando.id_comando_player,
                                    comandoIniciado,
                                    reporteExitoso);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error al consultar los comandos pendientes del Player.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Detención normal del servicio.
            }
            finally
            {
                logger.LogInformation("Servicio de comandos del Player finalizado.");
            }
        }
    }
}
