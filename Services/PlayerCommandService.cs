using DigitalSignage.Agent.Configuration;
using DigitalSignage.Agent.Models;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Consulta periódicamente los comandos del Player y delega su ejecución
    /// al método correspondiente, conservando el registro de errores.
    /// </summary>
    public sealed class PlayerCommandService(
        ApiCommunicationService apiCommunicationService,
        PlayerInformationService playerInformationService,
        PlayerTokenService playerTokenService,
        PlayerTokenRenewalService playerTokenRenewalService,
        PlayerTokenRenewalStateService renewalStateService,
        HardwareService hardwareService,
        IOptions<AgentOptions> agentOptions,
        ILogger<PlayerCommandService> logger,
        PlayerAdministrativeState playerAdministrativeState) : BackgroundService
    {
        /// <summary>
        /// Consulta los comandos a intervalos configurados hasta que se detenga el Agent.
        /// </summary>
        /// <param name="stoppingToken">Token de cancelación del servicio.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Servicio de comandos del Player iniciado.");

            int intervaloSegundos = agentOptions.Value.Commands.CheckIntervalSeconds;
            logger.LogInformation("Intervalo de consulta de comandos: {Intervalo} segundos.", intervaloSegundos);

            using PeriodicTimer timer = new(TimeSpan.FromSeconds(intervaloSegundos));

            try
            {
                do
                {
                    try
                    {
                        // Consultar comandos únicamente cuando la API
                        // haya confirmado que el Player está activo.
                        if (!playerAdministrativeState.EstaActivo)
                        {
                            logger.LogDebug(
                                "Consulta de comandos omitida. Player sin autorización administrativa activa.");
                        }
                        else
                        {
                            string? token = await playerTokenService.ObtenerTokenAsync(stoppingToken);

                            if (string.IsNullOrWhiteSpace(token))
                            {
                                logger.LogDebug(
                                    "Consulta de comandos omitida. El Player todavía no tiene un token dinámico.");
                            }
                            else
                            {
                                // Reconciliar primero cualquier renovación interrumpida.
                                await RecuperarRenovacionPendienteAsync(stoppingToken);

                                List<PlayerComandoPendiente> comandos =
                                    await apiCommunicationService.ObtenerComandosPendientesAsync(stoppingToken);

                                foreach (PlayerComandoPendiente comando in comandos)
                                {
                                    if (string.Equals(
                                        comando.codigo_tipo_comando,
                                        "RENOVAR_TOKEN",
                                        StringComparison.OrdinalIgnoreCase))
                                    {
                                        await ProcesarRenovacionTokenAsync(comando, stoppingToken);
                                    }
                                    else if (string.Equals(
                                        comando.codigo_tipo_comando,
                                        "REPORTAR_INFO",
                                        StringComparison.OrdinalIgnoreCase))
                                    {
                                        await ProcesarReporteInformacionAsync(comando, stoppingToken);
                                    }
                                    else
                                    {
                                        logger.LogWarning(
                                            "Tipo de comando no reconocido. Id: {IdComando}, Tipo: {TipoComando}.",
                                            comando.id_comando_player,
                                            comando.codigo_tipo_comando);
                                    }
                                }
                            }
                        }
                    }catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested){
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error al consultar los comandos pendientes del Player.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Detención normal del servicio.
            }
            finally
            {
                logger.LogInformation("Servicio de comandos del Player finalizado.");
            }
        }

        /// <summary>
        /// Reconciliación idempotente de una renovación interrumpida.
        /// Nunca repite RSA ni elimina el registro si no se comprueba el token guardado.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación del servicio.</param>
        /// <returns>Tarea que representa la recuperación.</returns>
        private async Task RecuperarRenovacionPendienteAsync(CancellationToken cancellationToken)
        {
            PlayerTokenRenewalState? estado = await renewalStateService.ObtenerAsync(cancellationToken);
            if (estado == null)
            {
                return;
            }

            int idComando = estado.IdComandoPlayer;

            if (!string.Equals(estado.CodigoEstado, "TokenGuardado", StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "RENOVAR_TOKEN {IdComando} requiere revisión manual: estado local {Estado}. No se repetirá RSA.",
                    idComando, estado.CodigoEstado);
                return;
            }

            string? tokenActual = await playerTokenService.ObtenerTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(tokenActual) || string.IsNullOrWhiteSpace(estado.TokenHash))
            {
                logger.LogError(
                    "RENOVAR_TOKEN {IdComando}: falta el token local o su hash. Se conserva el estado para revisión.",
                    idComando);
                return;
            }

            string hashActual = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(tokenActual)));

            if (!string.Equals(hashActual, estado.TokenHash, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "RENOVAR_TOKEN {IdComando}: el token local no coincide con el registrado. No se elimina el estado.",
                    idComando);
                return;
            }

            PlayerComandoEstado remoto = await apiCommunicationService.ConsultarEstadoComandoPlayerAsync(
                idComando, cancellationToken);

            if (string.Equals(remoto.codigo_estatus_comando, "Completado", StringComparison.OrdinalIgnoreCase))
            {
                await renewalStateService.EliminarAsync(cancellationToken);
                logger.LogInformation("RENOVAR_TOKEN {IdComando} reconciliado: ya estaba completado.", idComando);
                return;
            }

            if (remoto.expirado ||
                string.Equals(remoto.codigo_estatus_comando, "Expirado", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "RENOVAR_TOKEN {IdComando} expiró con token local guardado. Requiere revisión; no se elimina el estado.",
                    idComando);
                return;
            }

            if (string.Equals(remoto.codigo_estatus_comando, "En_proceso", StringComparison.OrdinalIgnoreCase))
            {
                await apiCommunicationService.FinalizarComandoPlayerAsync(
                    idComando, "Completado", "Token del Player renovado correctamente.", cancellationToken);
                await renewalStateService.EliminarAsync(cancellationToken);
                logger.LogInformation("RENOVAR_TOKEN {IdComando} recuperado y completado.", idComando);
                return;
            }

            logger.LogWarning(
                "RENOVAR_TOKEN {IdComando} tiene estado remoto {Estado}. Se conserva la recuperación local.",
                idComando, remoto.codigo_estatus_comando);
        }

        /// <summary>
        /// Procesa RENOVAR_TOKEN, verifica su estado remoto y confirma el resultado
        /// cuando la renovación local se ha completado.
        /// </summary>
        /// <param name="comando">Comando de renovación asignado al Player.</param>
        /// <param name="cancellationToken">Token de cancelación del servicio.</param>
        /// <returns>Tarea que representa el procesamiento del comando.</returns>
        private async Task ProcesarRenovacionTokenAsync(PlayerComandoPendiente comando, CancellationToken cancellationToken)
        {
            try
            {
                int idComando = comando.id_comando_player;

                logger.LogInformation(
                    "Procesando RENOVAR_TOKEN. Comando: {IdComando}.", idComando);

                PlayerComandoEstado estadoRemoto =
                    await apiCommunicationService.ConsultarEstadoComandoPlayerAsync(
                        idComando, cancellationToken);

                if (estadoRemoto.expirado ||
                    string.Equals(estadoRemoto.codigo_estatus_comando,
                        "Expirado", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("RENOVAR_TOKEN {IdComando} expiró. Se omite.", idComando);
                    return;
                }

                if (string.Equals(estadoRemoto.codigo_estatus_comando,
                    "Completado", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("RENOVAR_TOKEN {IdComando} ya está completado.", idComando);
                    return;
                }

                if (string.Equals(estadoRemoto.codigo_estatus_comando,
                    "Pendiente", StringComparison.OrdinalIgnoreCase))
                {
                    await apiCommunicationService.IniciarComandoPlayerAsync(
                        idComando, cancellationToken);
                }
                else if (!string.Equals(estadoRemoto.codigo_estatus_comando,
                    "En_proceso", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "RENOVAR_TOKEN {IdComando} tiene estado remoto {Estado}.",
                        idComando, estadoRemoto.codigo_estatus_comando);
                    return;
                }

                string numeroSerie = hardwareService.GetSerialNumber();

                await playerTokenRenewalService.RenovarTokenAsync(
                    idComando, numeroSerie, cancellationToken);

                // La confirmación se intenta solo después de guardar el token.
                // Si falla, no se vuelve a ejecutar RSA ni se informa Error por ese motivo.
                await apiCommunicationService.FinalizarComandoPlayerAsync(
                    idComando,
                    "Completado",
                    "Token del Player renovado correctamente.",
                    cancellationToken);

                // La API confirmó la finalización del comando.
                // Ya no necesitamos conservar su estado de recuperación.
                await renewalStateService.EliminarAsync(cancellationToken);

                logger.LogInformation("RENOVAR_TOKEN {IdComando} completado.", idComando);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "No fue posible completar RENOVAR_TOKEN {IdComando}. " +
                    "Se conservará el estado local para recuperación.",
                    comando.id_comando_player);
            }
        }

        /// <summary>
        /// Ejecuta REPORTAR_INFO y comunica a la API el resultado real del reporte.
        /// No repite la finalización si falla su respuesta HTTP.
        /// </summary>
        /// <param name="comando">Comando de reporte asignado al Player.</param>
        /// <param name="cancellationToken">Token de cancelación del servicio.</param>
        /// <returns>Tarea que representa el procesamiento del comando.</returns>
        private async Task ProcesarReporteInformacionAsync(PlayerComandoPendiente comando, CancellationToken cancellationToken)
        {
            if (string.Equals(comando.codigo_estatus_comando,
                "En_proceso", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "El comando REPORTAR_INFO {IdComando} ya está En_proceso. " +
                    "Se omite su ejecución para evitar duplicar el reporte.",
                    comando.id_comando_player);
                return;
            }

            bool comandoIniciado = false;
            bool reporteExitoso = false;
            string mensajeResultado = string.Empty;

            try
            {
                logger.LogInformation(
                    "Iniciando comando REPORTAR_INFO. Id: {IdComando}.",
                    comando.id_comando_player);

                await apiCommunicationService.IniciarComandoPlayerAsync(
                    comando.id_comando_player, cancellationToken);
                comandoIniciado = true;

                try
                {
                    await playerInformationService.ReportarInformacionAsync(cancellationToken);
                    reporteExitoso = true;
                    mensajeResultado = "Información del Player reportada correctamente.";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    mensajeResultado = ex.Message;
                    logger.LogError(
                        ex,
                        "Falló el reporte de información del comando {IdComando}.",
                        comando.id_comando_player);
                }

                // El estado depende del reporte, no del éxito de la confirmación HTTP.
                await apiCommunicationService.FinalizarComandoPlayerAsync(
                    comando.id_comando_player,
                    reporteExitoso ? "Completado" : "Error",
                    mensajeResultado,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // No se intenta finalizar nuevamente para evitar sobrescribir un éxito.
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
}
