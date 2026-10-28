using DigitalSignage.Agent.Configuration;
using DigitalSignage.Agent.Models;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Administra la comunicación HTTP entre DigitalSignage.Agent
    /// y la API de la plataforma Digital Signage.
    /// </summary>
    public sealed class ApiCommunicationService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ApiCommunicationService> _logger;
        private readonly ApiOptions _apiOptions;

        /// <summary>
        /// Inicializa el servicio de comunicación con la API.
        /// </summary>
        /// <param name="httpClientFactory">
        /// Fábrica utilizada para crear clientes HTTP administrados.
        /// </param>
        /// <param name="options">
        /// Configuración general del agente.
        /// </param>
        /// <param name="logger">
        /// Servicio de registro de eventos y errores.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Se produce cuando alguna dependencia requerida es nula.
        /// </exception>
        public ApiCommunicationService(IHttpClientFactory httpClientFactory, IOptions<AgentOptions> options, ILogger<ApiCommunicationService> logger)
        {
            _httpClientFactory = httpClientFactory
                ?? throw new ArgumentNullException(nameof(httpClientFactory));

            ArgumentNullException.ThrowIfNull(options);

            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));

            _apiOptions = options.Value.Api;
        }

        /// <summary>
        /// Realiza una solicitud HTTP GET a un endpoint de la API.
        /// </summary>
        /// <param name="endpoint">
        /// Ruta relativa del recurso que se desea consultar.
        /// </param>
        /// <param name="cancellationToken">
        /// Token que permite cancelar la solicitud.
        /// </param>
        /// <returns>
        /// Contenido de la respuesta HTTP cuando la solicitud es exitosa.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la URL base de la API no está configurada.
        /// </exception>
        /// <exception cref="HttpRequestException">
        /// Se produce cuando existe un error de comunicación o la API
        /// devuelve un código de estado HTTP no exitoso.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Se produce cuando el proceso solicitante cancela la operación.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Se produce cuando la solicitud supera el tiempo máximo configurado.
        /// </exception>
        public async Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiOptions.BaseUrl))
            {
                throw new InvalidOperationException(
                    "La URL base de la API no está configurada en Agent:Api:BaseUrl.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

            using HttpClient httpClient = _httpClientFactory.CreateClient();

            httpClient.BaseAddress = new Uri(
                _apiOptions.BaseUrl.TrimEnd('/') + "/");

            httpClient.Timeout = TimeSpan.FromSeconds(_apiOptions.TimeoutSeconds);

            if (string.IsNullOrWhiteSpace(_apiOptions.Token))
            {
                throw new InvalidOperationException(
                    "El token de autenticación no está configurado en Agent:Api:Token.");
            }

            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiOptions.Token);
            try
            {
                _logger.LogDebug(
                    "Iniciando solicitud HTTP GET al endpoint {Endpoint}.",
                    endpoint);

                using HttpResponseMessage response = await httpClient.GetAsync(
                    endpoint,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                string content = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                _logger.LogDebug(
                    "Solicitud HTTP GET completada. Endpoint: {Endpoint}. Código HTTP: {StatusCode}.",
                    endpoint,
                    (int)response.StatusCode);

                return content;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                // La cancelación fue solicitada por el servicio que invocó
                // la operación, normalmente durante la detención del agente.
                // Se conserva la excepción original.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Si el agente no solicitó la cancelación, se interpreta
                // como un tiempo de espera agotado de la solicitud HTTP.
                // Se conserva la excepción original como InnerException.
                throw new TimeoutException(
                    $"La solicitud HTTP GET al endpoint '{endpoint}' " +
                    $"excedió el tiempo máximo de {_apiOptions.TimeoutSeconds} segundos.",
                    ex);
            }
        }


        /// <summary>
        /// Envía información en formato JSON a un endpoint de la API.
        /// </summary>
        /// <typeparam name="T">Tipo de información que se enviará.</typeparam>
        /// <param name="endpoint">Ruta relativa del endpoint.</param>
        /// <param name="data">Información que se enviará en el cuerpo de la solicitud.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Contenido de la respuesta HTTP.</returns>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la URL base no está configurada.
        /// </exception>
        /// <exception cref="HttpRequestException">
        /// Se produce cuando la API devuelve un código HTTP no exitoso.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Se produce cuando la solicitud excede el tiempo configurado.
        /// </exception>
        public async Task<string> PostAsync<T>(string endpoint, T data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiOptions.BaseUrl))
            {
                throw new InvalidOperationException(
                    "La URL base de la API no está configurada en Agent:Api:BaseUrl.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
            ArgumentNullException.ThrowIfNull(data);

            using HttpClient httpClient = _httpClientFactory.CreateClient();

            httpClient.BaseAddress = new Uri(
                _apiOptions.BaseUrl.TrimEnd('/') + "/");

            httpClient.Timeout = TimeSpan.FromSeconds(
                _apiOptions.TimeoutSeconds);

            if (string.IsNullOrWhiteSpace(_apiOptions.Token))
            {
                throw new InvalidOperationException(
                    "El token de autenticación no está configurado en Agent:Api:Token.");
            }

            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiOptions.Token);

            try
            {
                _logger.LogDebug(
                    "Iniciando solicitud HTTP POST al endpoint {Endpoint}.",
                    endpoint);

                using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                    endpoint.TrimStart('/'),
                    data,
                    cancellationToken);

                string content = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al consultar '{endpoint}'. " +
                        $"Respuesta del servidor: {content}");
                }

                _logger.LogDebug(
                    "Solicitud HTTP POST completada. Endpoint: {Endpoint}. Código HTTP: {StatusCode}.",
                    endpoint,
                    (int)response.StatusCode);

                return content;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException(
                    $"La solicitud HTTP POST al endpoint '{endpoint}' " +
                    $"excedió el tiempo máximo de {_apiOptions.TimeoutSeconds} segundos.",
                    ex);
            }
        }


        /// <summary>
        /// Consulta los comandos pendientes asignados al Player autenticado.
        /// </summary>
        /// <param name="cancellationToken">
        /// Token que permite cancelar la operación.
        /// </param>
        /// <returns>
        /// Lista de comandos pendientes recibidos desde la API.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la API devuelve una respuesta inválida
        /// o indica que la operación no fue exitosa.
        /// </exception>
        /// <exception cref="JsonException">
        /// Se produce cuando la respuesta no tiene un formato JSON válido.
        /// </exception>
        public async Task<List<PlayerComandoPendiente>> ObtenerComandosPendientesAsync(CancellationToken cancellationToken = default)
        {
            const string endpoint = "Agent/ObtenerComandosPendientes";

            string json = await GetAsync(endpoint, cancellationToken);

            ApiResponse<List<PlayerComandoPendiente>>? response = JsonSerializer.Deserialize<ApiResponse<List<PlayerComandoPendiente>>>(json);

            if (response == null)
            {
                throw new InvalidOperationException(
                    "La API devolvió una respuesta nula al consultar los comandos pendientes.");
            }

            if (!string.Equals(response.Status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"No fue posible consultar los comandos pendientes. " +
                    $"Respuesta de la API: {response.Message}");
            }

            if (response.Data == null)
            {
                throw new InvalidOperationException(
                    "La API respondió correctamente, pero no devolvió la lista de comandos pendientes.");
            }

            _logger.LogDebug(
                "Consulta de comandos pendientes completada. Total recibido: {TotalComandos}.",
                response.Data.Count);

            return response.Data;
        }

        /// <summary>
        /// Despues de que detecta un comando pendiente, este metodo solicita a la API el inicio de dicho comando asignado al Player autenticado.
        /// </summary>
        /// <param name="idComandoPlayer">
        /// Identificador del comando que se desea iniciar.
        /// </param>
        /// <param name="cancellationToken">
        /// Token que permite cancelar la operación.
        /// </param>
        /// <returns>
        /// Tarea que representa la operación de inicio del comando.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Se produce cuando el identificador del comando no es válido.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la API devuelve una respuesta inválida
        /// o rechaza el inicio del comando.
        /// </exception>
        /// <exception cref="JsonException">
        /// Se produce cuando la respuesta no contiene JSON válido.
        /// </exception>
        public async Task IniciarComandoPlayerAsync(int idComandoPlayer, CancellationToken cancellationToken = default)
        {
            if (idComandoPlayer <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(idComandoPlayer),
                    "El identificador del comando debe ser mayor que cero.");
            }

            const string endpoint = "Agent/IniciarComandoPlayer";

            //Se envia el parametro al endpoint de la API para que inicie el comando pendiente asignado al Player autenticado.
            PlayerComandoIniciarRequest request = new()
            {
                id_comando_player = idComandoPlayer
            };

            string json = await PostAsync(endpoint, request, cancellationToken);

            ApiResponse<object>? response = JsonSerializer.Deserialize<ApiResponse<object>>(json);

            if (response == null)
            {
                throw new InvalidOperationException(
                    $"La API devolvió una respuesta nula al iniciar el comando {idComandoPlayer}.");
            }

            if (!string.Equals(
                response.Status,
                "OK",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"No fue posible iniciar el comando {idComandoPlayer}. " +
                    $"Respuesta de la API: {response.Message}");
            }

            _logger.LogInformation(
                "Comando {IdComando} iniciado correctamente. Respuesta: {Message}",
                idComandoPlayer,
                response.Message);
        }

        /// <summary>
        /// Registra en la API central el resultado final de un comando del Player, una vez que se ejecuta el comando registra si fue exitoso o no.
        /// </summary>
        /// <param name="idComandoPlayer">Identificador del comando.</param>
        /// <param name="codigoResultado">COMPLETADO o ERROR.</param>
        /// <param name="mensajeResultado">Descripción del resultado.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Se produce cuando el identificador no es válido.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la API rechaza la finalización.
        /// </exception>
        public async Task FinalizarComandoPlayerAsync(int idComandoPlayer, string codigoResultado, string mensajeResultado, CancellationToken cancellationToken = default)
        {
            if (idComandoPlayer <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(idComandoPlayer));
            }

            if (codigoResultado != "Completado" && codigoResultado != "Error")
            {
                throw new ArgumentException(
                    "El resultado debe ser Completado o Error.",
                    nameof(codigoResultado));
            }

            //Se envian los parametros al endpoint de la API para que sea "Completado" el comando "En_proceso" asignado al Player autenticado.
            var request = new PlayerComandoFinalizarRequest
            {
                id_comando_player = idComandoPlayer,
                codigo_resultado = codigoResultado,
                mensaje_resultado = mensajeResultado
            };

            string response = await PostAsync(
                "Agent/FinalizarComandoPlayer",
                request,
                cancellationToken);

            ApiResponse<object>? result =
                JsonSerializer.Deserialize<ApiResponse<object>>(response);

            if (result == null ||
                !string.Equals(result.Status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"No fue posible finalizar el comando {idComandoPlayer}. " +
                    $"Respuesta de la API: {result?.Message ?? response}");
            }

            _logger.LogInformation(
                "Comando {IdComando} finalizado con resultado {Resultado}. Respuesta: {Message}",
                idComandoPlayer,
                codigoResultado,
                result.Message);
        }















    }
}