using DigitalSignage.Agent.Services;
using DigitalSignage.Agent.Configuration;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace DigitalSignage.Agent
{
    /// <summary>
    /// Servicio principal encargado de coordinar la ejecución
    /// de los módulos que conforman DigitalSignage.Agent.
    /// </summary>
    public class Worker(
        ILogger<Worker> logger,
        HardwareService hardwareService,
        NetworkService networkService,
        IOptions<AgentOptions> agentOptions,
        IOptions<PlayerOptions> playerOptions) : BackgroundService
    {
        /// <summary>
        /// Ejecuta el ciclo principal del agente hasta que
        /// el sistema solicita su detención.
        /// </summary>
        /// <param name="stoppingToken">
        /// Token que permite detener el servicio de forma controlada.
        /// </param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("DigitalSignage.Agent iniciado.");

            try
            {
                // Registrar la versión instalada del agente.
                logger.LogInformation("Versión de DigitalSignage.Agent: {VersionAgent}", AgentVersion.Current);

                // Obtiene y registra la identificación del dispositivo,
                // incluyendo modelo, número de serie, MAC y almacenamiento.
                LogHardwareInformation();

                // Identifica y registra las direcciones IPv4 disponibles
                // en las interfaces de red activas del dispositivo.
                LogNetworkInformation();

                // Inicia la supervisión periódica de la red para detectar
                // cambios de dirección IPv4, pérdidas y recuperaciones de ruta.
                await MonitorNetworkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Detención normal solicitada por el sistema.
            }
            catch (Exception ex)
            {
                // Registra errores inesperados y permite que el host
                // gestione el fallo del servicio.
                logger.LogCritical(ex, "Error inesperado en el servicio principal.");
                throw;
            }
            finally
            {
                // Registra la finalización del agente.
                logger.LogInformation("DigitalSignage.Agent finalizado.");
            }
        }

        // ============================================================================================
        // INFORMACIÓN DEL HARDWARE
        // ============================================================================================

        /// <summary>
        /// Obtiene y registra la información de identificación
        /// y almacenamiento del dispositivo Raspberry Pi.
        /// </summary>
        private void LogHardwareInformation()
        {
            // Obtener la identificación del Player.
            int idPlayer = playerOptions.Value.Id;

            // Obtener la identificación física del dispositivo.
            string model = hardwareService.GetModel();
            string serialNumber = hardwareService.GetSerialNumber();
            string macAddress = hardwareService.GetMacAddress();

            logger.LogInformation("Id player: {IdPlayer}", idPlayer);
            logger.LogInformation("Modelo: {Model}", model);
            logger.LogInformation("Número de serie: {SerialNumber}", serialNumber);
            logger.LogInformation("Dirección MAC: {MacAddress}", macAddress);

            // Consultar la capacidad y el espacio disponible de almacenamiento.
            long storageCapacity = hardwareService.GetStorageCapacityBytes();
            long availableStorage = hardwareService.GetAvailableStorageBytes();

            logger.LogInformation(
                "Capacidad física de microSD: {CapacityBytes} bytes.",
                storageCapacity);

            logger.LogInformation(
                "Espacio disponible para contenido: {AvailableBytes} bytes.",
                availableStorage);
        }

        // ============================================================================================
        // INFORMACIÓN DE RED
        // ============================================================================================

        /// <summary>
        /// Obtiene y registra las direcciones IPv4 disponibles
        /// en las interfaces de red activas del dispositivo.
        /// </summary>
        private void LogNetworkInformation()
        {
            // Consultar las direcciones IPv4 disponibles al iniciar el agente.
            var ipv4Addresses = networkService.GetIPv4Addresses();

            if (ipv4Addresses.Count == 0)
            {
                logger.LogWarning(
                    "No se encontraron direcciones IPv4 en las interfaces de red activas.");

                return;
            }

            // Registrar cada dirección IPv4 encontrada.
            foreach (var address in ipv4Addresses)
            {
                logger.LogInformation(
                    "Dirección IPv4 detectada: {IPAddress}",
                    address);
            }
        }

        // ============================================================================================
        // SUPERVISIÓN DE RED
        // ============================================================================================

        /// <summary>
        /// Supervisa periódicamente la ruta IPv4 seleccionada por Linux
        /// para detectar cambios de dirección, pérdidas y recuperaciones de ruta.
        /// </summary>
        /// <param name="stoppingToken">
        /// Token utilizado para detener la supervisión cuando el sistema lo solicita.
        /// </param>
        /// <returns>
        /// Tarea que representa el proceso continuo de supervisión de red.
        /// </returns>
        private async Task MonitorNetworkAsync(CancellationToken stoppingToken)
        {
            // Obtener el intervalo de supervisión definido en appsettings.json.
            TimeSpan networkCheckInterval = TimeSpan.FromSeconds(
                agentOptions.Value.Network.CheckIntervalSeconds);

            logger.LogInformation(
                "Intervalo de supervisión de red configurado: {IntervalSeconds} segundos.",
                networkCheckInterval.TotalSeconds);

            // Destino temporal para determinar la ruta IPv4 seleccionada por Linux.
            // Posteriormente utilizaremos el destino de nuestra API configurada.
            IPAddress destination = IPAddress.Parse("1.1.1.1");

            // Última dirección IPv4 seleccionada por el sistema operativo.
            IPAddress? lastKnownAddress = null;

            // Indica si ya se realizó una detección inicial satisfactoria.
            bool initialDetectionCompleted = false;

            // Indica si se detectó previamente una pérdida de ruta.
            bool routeLost = false;

            // Ejecutar la supervisión mientras el servicio permanezca activo.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Consultar la dirección IPv4 utilizada por Linux
                    // para alcanzar el destino configurado.
                    IPAddress currentAddress =
                        networkService.GetPreferredIPv4Address(destination);

                    if (!initialDetectionCompleted)
                    {
                        // Registrar la primera ruta IPv4 detectada.
                        logger.LogInformation(
                            "Ruta IPv4 inicial detectada. Dirección: {IPAddress}",
                            currentAddress);

                        initialDetectionCompleted = true;
                    }
                    else if (routeLost)
                    {
                        // Registrar la recuperación de la ruta después de una pérdida.
                        logger.LogInformation(
                            "Ruta IPv4 recuperada. Dirección actual: {IPAddress}",
                            currentAddress);
                    }
                    else if (!Equals(lastKnownAddress, currentAddress))
                    {
                        // Registrar únicamente los cambios de dirección IPv4.
                        logger.LogInformation(
                            "Dirección IPv4 modificada. Anterior: {PreviousIP}, Actual: {CurrentIP}",
                            lastKnownAddress,
                            currentAddress);
                    }

                    // Actualizar el estado de la última ruta detectada.
                    lastKnownAddress = currentAddress;
                    routeLost = false;
                }
                catch (SocketException ex)
                {
                    // Registrar una pérdida de ruta solamente una vez,
                    // evitando generar advertencias repetidas en cada ciclo.
                    if (!routeLost)
                    {
                        logger.LogWarning(
                            ex,
                            "Ruta IPv4 no disponible. El agente continuará supervisando la red.");

                        routeLost = true;
                        lastKnownAddress = null;
                    }
                }

                // Esperar el intervalo configurado antes de realizar
                // una nueva comprobación, permitiendo la cancelación del servicio.
                await Task.Delay(networkCheckInterval, stoppingToken);
            }
        }
    }
}