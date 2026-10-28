using DigitalSignage.Agent.Configuration;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Recopila y envía la información actual del Player a la API central.
    /// Puede utilizarse desde el reporte automático o desde un comando.
    /// </summary>
    public sealed class PlayerInformationService
    {
        private readonly ApiCommunicationService _apiCommunicationService;
        private readonly HardwareService _hardwareService;
        private readonly NetworkService _networkService;
        private readonly IOptions<AgentOptions> _agentOptions;
        private readonly IOptions<PlayerOptions> _playerOptions;
        private readonly ILogger<PlayerInformationService> _logger;

        /// <summary>
        /// Inicializa el servicio encargado de reportar la información del Player.
        /// </summary>
        public PlayerInformationService(
            ApiCommunicationService apiCommunicationService,
            HardwareService hardwareService,
            NetworkService networkService,
            IOptions<AgentOptions> agentOptions,
            IOptions<PlayerOptions> playerOptions,
            ILogger<PlayerInformationService> logger)
        {
            _apiCommunicationService = apiCommunicationService;
            _hardwareService = hardwareService;
            _networkService = networkService;
            _agentOptions = agentOptions;
            _playerOptions = playerOptions;
            _logger = logger;
        }

        /// <summary>
        /// Obtiene los datos actuales del dispositivo y los reporta a la API.
        /// </summary>
        /// <param name="cancellationToken">
        /// Token para cancelar la operación durante la detención del Agent.
        /// </param>
        /// <returns>Tarea que representa el envío y validación del reporte.</returns>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la API rechaza el reporte.
        /// </exception>
        /// <exception cref="HttpRequestException">
        /// Se produce cuando falla la comunicación HTTP.
        /// </exception>
        /// <exception cref="JsonException">
        /// Se produce cuando la respuesta JSON es inválida.
        /// </exception>
        public async Task ReportarInformacionAsync(CancellationToken cancellationToken = default)
        {
            int idPlayer = _playerOptions.Value.Id;

            var apiUri = new Uri(_agentOptions.Value.Api.BaseUrl);

            IPAddress ipAddress = _networkService.GetPreferredIPv4Address(
                IPAddress.Parse(apiUri.Host));

            var report = new
            {
                id_player = idPlayer,
                numero_serie = _hardwareService.GetSerialNumber(),
                direccion_mac = _networkService.GetMacAddressByIPv4(ipAddress),
                modelo_player = _hardwareService.GetModel(),
                ip_player = ipAddress.ToString(),
                version_agent = AgentVersion.Current,
                capacidad_almacenamiento_bytes = _hardwareService.GetStorageCapacityBytes(),
                espacio_disponible_bytes = _hardwareService.GetAvailableStorageBytes()
            };
            // Se envía la información del Player a la API central para su registro y seguimiento.
            string response = await _apiCommunicationService.PostAsync(
                "Agent/ReportarInformacion",
                report,
                cancellationToken);

            using JsonDocument json = JsonDocument.Parse(response);

            if (!json.RootElement.TryGetProperty("Status", out JsonElement status) ||
                status.GetString() != "OK")
            {
                string message = json.RootElement.TryGetProperty(
                    "Message", out JsonElement errorMessage)
                    ? errorMessage.ToString()
                    : response;

                throw new InvalidOperationException(
                    $"La API rechazó el reporte del Player: {message}");
            }

            _logger.LogInformation(
                "Información del Player reportada correctamente a la API central.");
        }
    }
}
