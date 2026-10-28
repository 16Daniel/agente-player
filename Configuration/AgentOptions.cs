using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Configuration
{
    /// <summary>
    /// Representa la configuración general del agente
    /// definida en la sección Agent de appsettings.json.
    /// </summary>
    public sealed class AgentOptions
    {

        /// <summary>
        /// Nombre de la sección de configuración.
        /// </summary>
        public const string SectionName = "Agent";

        /// <summary>
        /// Configuración de comunicación con la API.
        /// </summary>
        public ApiOptions Api { get; set; } = new();

        /// <summary>
        /// Configuración de supervisión de red.
        /// </summary>
        public NetworkOptions Network { get; set; } = new();

        /// <summary>
        /// Configuración de consulta de comandos enviados al Player.
        /// </summary>
        public CommandOptions Commands { get; set; } = new();
    }

    /// <summary>
    /// Representa los parámetros de comunicación
    /// entre el agente y la plataforma web.
    /// </summary>
    public sealed class ApiOptions
    {
        /// <summary>
        /// Dirección base HTTPS de la plataforma web.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Tiempo máximo de espera para las solicitudes HTTP,
        /// expresado en segundos.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Intervalo, en segundos, entre verificaciones de disponibilidad de la API.
        /// </summary>
        public int CheckIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Token Bearer utilizado para autenticar al Player ante la API.
        /// </summary>
        public string Token { get; set; } = string.Empty;
    }

    /// <summary>
    /// Representa los parámetros utilizados para
    /// supervisar periódicamente la red.
    /// </summary>
    public sealed class NetworkOptions
    {
        /// <summary>
        /// Intervalo entre verificaciones de red,
        /// expresado en segundos.
        /// </summary>
        public int CheckIntervalSeconds { get; set; } = 30;
    }
    /// <summary>
    /// Representa los parámetros utilizados para
    /// supervisar periódicamente la red.
    /// </summary>
    public sealed class PlayerOptions
    {
        /// <summary>
        /// Nombre de la sección de configuración.
        /// </summary>
        public const string SectionName = "Informacion";

        /// <summary>
        /// Intervalo entre verificaciones de red,
        /// expresado en segundos.
        /// </summary>
        public int Id { get; set; } = 0;
    }

    /// <summary>
    /// Configuración del servicio de comandos del Player.
    /// </summary>
    public sealed class CommandOptions
    {
        /// <summary>
        /// Intervalo en segundos para consultar comandos pendientes en la API central.
        /// </summary>
        public int CheckIntervalSeconds { get; set; } = 5;
    }
}

