using DigitalSignage.Agent.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Supervisa la disponibilidad de la API, gestiona la solicitud y consulta
    /// de vinculación y coordina el reporte de información del Player.
    /// </summary>
    public sealed class ApiMonitoringService(
        ILogger<ApiMonitoringService> logger,
        ApiCommunicationService apiCommunicationService,
        IOptions<AgentOptions> agentOptions,
        PlayerInformationService playerInformationService,
        HardwareService hardwareService,
        DeviceIdentityService deviceIdentityService,
        PlayerTokenService playerTokenService,
        PlayerAdministrativeState playerAdministrativeState) : BackgroundService
    {
        private bool? _ultimaDisponibilidad;
        private int? _ultimoEstadoVinculacion;

        /// <summary>
        /// Ejecuta el ciclo periódico de supervisión usando el intervalo configurado.
        /// </summary>
        /// <param name="stoppingToken">Token de cancelación del servicio.</param>
        /// <returns>Tarea que representa la ejecución del servicio.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            TimeSpan checkInterval = TimeSpan.FromSeconds(
                agentOptions.Value.Api.CheckIntervalSeconds);

            logger.LogInformation(
                "Supervisión de API iniciada. Intervalo: {Seconds} segundos.",
                checkInterval.TotalSeconds);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {

                    if (await VerificarDisponibilidadAsync(stoppingToken))
                    {
                        int? estadoVinculacion = await ConsultarEstadoVinculacionAsync(stoppingToken);

                        // Registrar únicamente cuando la API confirme que no existe.
                        if (estadoVinculacion == 0){
                            await SolicitarVinculacionAsync(stoppingToken);
                        }

                        // Solo los Players autorizados pueden autenticarse y reportar.
                        if (estadoVinculacion == 2){
                            bool autenticado = await AsegurarAutenticacionAsync(stoppingToken);

                            if (autenticado)
                            {
                                await ReportarInformacionAsync(stoppingToken);
                            }
                        }
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

        /// <summary>
        /// Comprueba la disponibilidad de la API y registra cambios de conectividad.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>True cuando la API responde correctamente; false en caso contrario.</returns>
        private async Task<bool> VerificarDisponibilidadAsync(CancellationToken cancellationToken)
        {
            try
            {
                await apiCommunicationService.ConsultarSaludAsync(cancellationToken);

                if (_ultimaDisponibilidad != true)
                {
                    logger.LogInformation(
                        _ultimaDisponibilidad == false
                            ? "Comunicación con la API central recuperada."
                            : "Comunicación inicial con la API central establecida.");
                }

                _ultimaDisponibilidad = true;
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_ultimaDisponibilidad != false)
                {
                    logger.LogWarning(ex,
                        "API central no disponible. El agente continuará funcionando.");
                }

                _ultimaDisponibilidad = false;
                return false;
            }
        }

        /// <summary>
        /// Envía una solicitud de vinculación cuando la API confirma
        /// que el dispositivo no tiene una solicitud registrada.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Tarea que representa la operación.</returns>
        private async Task SolicitarVinculacionAsync(CancellationToken cancellationToken)
        {
           

            try
            {
                string numeroSerie = hardwareService.GetSerialNumber();
                string clavePublica = deviceIdentityService.GetPublicKey();

                string respuesta = await apiCommunicationService.SolicitarVinculacionAsync(
                    numeroSerie, clavePublica, cancellationToken);

                using JsonDocument documento = JsonDocument.Parse(respuesta);
                JsonElement raiz = documento.RootElement;
                string? status = raiz.GetProperty("Status").GetString();
                int codigo = raiz.GetProperty("Data").GetProperty("Codigo").GetInt32();

                if (string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase)
                    && (codigo == 1 || codigo == 2))
                {
                   
                    logger.LogInformation(
                        "Solicitud de vinculación confirmada. Código: {Codigo}.", codigo);
                }
                else
                {
                    logger.LogWarning(
                        "Solicitud de vinculación no confirmada. Respuesta: {Respuesta}", respuesta);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "No fue posible confirmar la solicitud de vinculación. Se reintentará cuando corresponda.");
            }
        }

        /// <summary>
        /// Consulta el estado de vinculación del dispositivo y registra
        /// los cambios de estado detectados.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>
        /// Código de vinculación (0 a 3), o null si ocurre un error.
        /// </returns>
        private async Task<int?> ConsultarEstadoVinculacionAsync(CancellationToken cancellationToken)
        {
            try
            {
                string numeroSerie = hardwareService.GetSerialNumber();
                string clavePublica = deviceIdentityService.GetPublicKey();

                string respuesta = await apiCommunicationService.ConsultarEstadoVinculacionAsync(
                    numeroSerie, clavePublica, cancellationToken);

                using JsonDocument documento = JsonDocument.Parse(respuesta);
                JsonElement raiz = documento.RootElement;
                string? status = raiz.GetProperty("Status").GetString();

                if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"La consulta de vinculación no fue exitosa. Respuesta: {respuesta}");
                }

                JsonElement data = raiz.GetProperty("Data");
                int codigo = data.GetProperty("Codigo").GetInt32();
                string? estado = data.GetProperty("Estado").GetString();

                if (codigo < 0 || codigo > 4)
                {
                    throw new InvalidOperationException(
                        $"La API devolvió un código de vinculación desconocido: {codigo}.");
                }

                // Permitir operaciones únicamente cuando la API confirma
                // que la identidad del Player está autorizada y activa.
                playerAdministrativeState.EstablecerEstado(codigo == 2);

                if (_ultimoEstadoVinculacion == codigo)
                {
                    return codigo;
                }

                _ultimoEstadoVinculacion = codigo;

                switch (codigo)
                {
                    case 0:
                        logger.LogWarning(
                            "No existe una solicitud de vinculación para este dispositivo.");
                        break;
                    case 1:
                        logger.LogInformation("Vinculación pendiente de autorización.");
                        break;
                    case 2:
                        logger.LogInformation(
                            "Vinculación autorizada. Pendiente establecer autenticación del Agent.");
                        break;
                    case 3:
                        logger.LogWarning("Solicitud de vinculación rechazada.");
                        break;
                    case 4:
                        logger.LogWarning("Player administrativamente inactivo o suspendido. " +
                            "Las operaciones permanecerán bloqueadas hasta su reactivación.");
                        break;
                }

                logger.LogInformation(
                    "Estado de vinculación actualizado. Código: {Codigo}, Estado: {Estado}.",
                    codigo, estado);

                return codigo;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "No fue posible consultar el estado de vinculación del dispositivo.");
                return null;
            }
        }

        /// <summary>
        /// Garantiza que el Player tenga un token de autenticación local.
        /// Si no existe, realiza el intercambio RSA y almacena el token.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación del servicio.</param>
        /// <returns>
        /// True cuando existe un token local o se obtuvo uno nuevo;
        /// false cuando no fue posible completar la autenticación.
        /// </returns>
        private async Task<bool> AsegurarAutenticacionAsync(CancellationToken cancellationToken)
        {
            try
            {
                string? token = await playerTokenService.ObtenerTokenAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(token))
                {
                    return true;
                }

                string numeroSerie = hardwareService.GetSerialNumber();

                logger.LogInformation("Player autorizado sin token local. Iniciando autenticación RSA.");

                string nuevoToken = await apiCommunicationService.AutenticarConRsaAsync(numeroSerie, cancellationToken);

                await playerTokenService.GuardarTokenAsync(nuevoToken, cancellationToken);

                logger.LogInformation("Autenticación RSA completada. Token dinámico almacenado.");

                return true;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No fue posible autenticar al Player. Se reintentará en el siguiente ciclo.");
                return false;
            }
        }

        /// <summary>
        /// Reporta la información del Player a la API sin detener la supervisión
        /// cuando el reporte falla.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Tarea que representa la operación.</returns>
        private async Task ReportarInformacionAsync(CancellationToken cancellationToken)
        {
            try
            {
                // SP: SpOpMxDS_ActualizarInformacionPlayer.
                await playerInformationService.ReportarInformacionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "No fue posible reportar la información del Player a la API central.");
            }
        }
    }
}
