using DigitalSignage.Agent.Configuration;
using DigitalSignage.Agent.Models;
using Microsoft.Extensions.Options;
using System.Net;
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
        private readonly DeviceIdentityService _deviceIdentityService;
        private readonly PlayerTokenService _playerTokenService;

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
        public ApiCommunicationService(IHttpClientFactory httpClientFactory, IOptions<AgentOptions> options, ILogger<ApiCommunicationService> logger, DeviceIdentityService deviceIdentityService, PlayerTokenService playerTokenService)
        {
            _httpClientFactory = httpClientFactory
                ?? throw new ArgumentNullException(nameof(httpClientFactory));

            ArgumentNullException.ThrowIfNull(options);

            _deviceIdentityService = deviceIdentityService
                ?? throw new ArgumentNullException(nameof(deviceIdentityService));

            _playerTokenService = playerTokenService
                ?? throw new ArgumentNullException(nameof(playerTokenService));

            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));

            _apiOptions = options.Value.Api;
        }

        /// <summary>
        /// Envía una solicitud de vinculación del dispositivo a la API.
        /// No requiere Bearer Token porque el Player aún no está autorizado.
        /// </summary>
        /// <param name="numeroSerie">Número de serie de la Raspberry.</param>
        /// <param name="clavePublica">Clave pública RSA en formato PEM.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Respuesta JSON de la API.</returns>
        public async Task<string> SolicitarVinculacionAsync(string numeroSerie, string clavePublica, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(numeroSerie);
            ArgumentException.ThrowIfNullOrWhiteSpace(clavePublica);

            if (string.IsNullOrWhiteSpace(_apiOptions.BaseUrl))
            {
                throw new InvalidOperationException(
                    "La URL base de la API no está configurada.");
            }

            const string endpoint = "Agent/SolicitarVinculacion";

            using HttpClient httpClient = _httpClientFactory.CreateClient();

            httpClient.BaseAddress = new Uri(
                _apiOptions.BaseUrl.TrimEnd('/') + "/");

            httpClient.Timeout = TimeSpan.FromSeconds(
                _apiOptions.TimeoutSeconds);

            var request = new
            {
                numero_serie = numeroSerie,
                clave_publica = clavePublica
            };

            try
            {
                _logger.LogInformation(
                    "Enviando solicitud de vinculación del dispositivo {NumeroSerie}.",
                    numeroSerie);

                using HttpResponseMessage response =
                    await httpClient.PostAsJsonAsync(
                        endpoint,
                        request,
                        cancellationToken);

                string content = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al solicitar vinculación. " +
                        $"Respuesta del servidor: {content}");
                }

                _logger.LogInformation(
                    "Solicitud de vinculación enviada correctamente. Código HTTP: {StatusCode}.",
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
                    $"La solicitud de vinculación excedió el tiempo máximo " +
                    $"de {_apiOptions.TimeoutSeconds} segundos.",
                    ex);
            }
        }

        /// <summary>
        /// Consulta el estado actual de vinculación del dispositivo.
        /// No requiere Bearer Token porque el Player aún puede estar pendiente
        /// de autorización.
        /// </summary>
        /// <param name="numeroSerie">Número de serie del dispositivo.</param>
        /// <param name="clavePublica">Clave pública RSA en formato PEM.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Respuesta JSON de la API.</returns>
        /// <exception cref="ArgumentException">
        /// Se produce cuando los datos de identidad son inválidos.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la URL base no está configurada.
        /// </exception>
        /// <exception cref="HttpRequestException">
        /// Se produce cuando la API devuelve un error HTTP.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Se produce cuando la consulta excede el tiempo configurado.
        /// </exception>
        public async Task<string> ConsultarEstadoVinculacionAsync(string numeroSerie, string clavePublica, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(numeroSerie);
            ArgumentException.ThrowIfNullOrWhiteSpace(clavePublica);

            if (string.IsNullOrWhiteSpace(_apiOptions.BaseUrl))
            {
                throw new InvalidOperationException(
                    "La URL base de la API no está configurada en Agent:Api:BaseUrl.");
            }

            const string endpoint = "Agent/ConsultarEstadoVinculacion";

            using HttpClient httpClient = _httpClientFactory.CreateClient();

            httpClient.BaseAddress = new Uri(
                _apiOptions.BaseUrl.TrimEnd('/') + "/");

            httpClient.Timeout = TimeSpan.FromSeconds(
                _apiOptions.TimeoutSeconds);

            var request = new
            {
                numero_serie = numeroSerie,
                clave_publica = clavePublica
            };

            try
            {
                using HttpResponseMessage response =
                    await httpClient.PostAsJsonAsync(
                        endpoint,
                        request,
                        cancellationToken);

                string content = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al consultar el estado de vinculación. " +
                        $"Respuesta del servidor: {content}");
                }

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
                    $"La consulta de vinculación excedió el tiempo máximo " +
                    $"de {_apiOptions.TimeoutSeconds} segundos.",
                    ex);
            }
        }

        /// <summary>
        /// Solicita a la API un desafío criptográfico para autenticar
        /// la identidad RSA del Player previamente autorizado.
        /// No requiere Bearer Token.
        /// </summary>
        /// <param name="numeroSerie">Número de serie de la Raspberry.</param>
        /// <param name="clavePublica">Clave pública RSA en formato PEM.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Respuesta JSON que contiene el desafío y su identificador.</returns>
        public async Task<string> SolicitarDesafioAsync(string numeroSerie, string clavePublica, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(numeroSerie);
            ArgumentException.ThrowIfNullOrWhiteSpace(clavePublica);

            if (string.IsNullOrWhiteSpace(_apiOptions.BaseUrl))
            {
                throw new InvalidOperationException(
                    "La URL base de la API no está configurada en Agent:Api:BaseUrl.");
            }

            const string endpoint = "Agent/SolicitarDesafio";

            using HttpClient httpClient = _httpClientFactory.CreateClient();

            httpClient.BaseAddress = new Uri(_apiOptions.BaseUrl.TrimEnd('/') + "/");

            // Establece el tiempo de espera para la solicitud HTTP según la configuración.
            httpClient.Timeout = TimeSpan.FromSeconds(_apiOptions.TimeoutSeconds);

            var request = new
            {
                numero_serie = numeroSerie,
                clave_publica = clavePublica
            };

            try
            {
                _logger.LogInformation("Solicitando desafío RSA para el dispositivo {NumeroSerie}.", numeroSerie);

                // Se envía la solicitud POST al endpoint de la API para obtener el desafío RSA.
                using HttpResponseMessage response = await httpClient.PostAsJsonAsync(endpoint, request, cancellationToken);

                // Devuelve el JSON con IdDesafio y Desafio, que utilizaremos para generar la firma RSA.
                string content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al solicitar desafío RSA. " +
                        $"Respuesta del servidor: {content}");
                }

                _logger.LogInformation(
                    "Desafío RSA solicitado correctamente para {NumeroSerie}.",
                    numeroSerie);

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
                    $"La solicitud del desafío RSA excedió el tiempo máximo " +
                    $"de {_apiOptions.TimeoutSeconds} segundos.",
                    ex);
            }
        }

        /// <summary>
        /// Envía a la API la firma digital del desafío RSA para
        /// autenticar al Player y obtener un token Bearer.
        /// No requiere un token de autenticación previo.
        /// </summary>
        /// <param name="idDesafio">Identificador del desafío recibido.</param>
        /// <param name="firma">Firma RSA-PSS/SHA-256 codificada en Base64.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>
        /// Respuesta JSON con el token Bearer y el identificador del Player.
        /// </returns>
        public async Task<string> AutenticarPlayerAsync(string idDesafio, string firma, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idDesafio);
            ArgumentException.ThrowIfNullOrWhiteSpace(firma);

            if (string.IsNullOrWhiteSpace(_apiOptions.BaseUrl))
            {
                throw new InvalidOperationException(
                    "La URL base de la API no está configurada en Agent:Api:BaseUrl.");
            }

            const string endpoint = "Agent/AutenticarPlayer";

            using HttpClient httpClient = _httpClientFactory.CreateClient();

            httpClient.BaseAddress = new Uri(_apiOptions.BaseUrl.TrimEnd('/') + "/");

            // Establece el tiempo de espera para la solicitud HTTP según la configuración.
            httpClient.Timeout = TimeSpan.FromSeconds(_apiOptions.TimeoutSeconds);

            var request = new
            {
                id_desafio = idDesafio,
                firma = firma
            };

            try
            {
                _logger.LogInformation("Enviando firma RSA para autenticar al Player.");

                using HttpResponseMessage response = await httpClient.PostAsJsonAsync(endpoint, request, cancellationToken);

                string content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al autenticar el Player. " +
                        $"Respuesta del servidor: {content}");
                }

                _logger.LogInformation(
                    "La API respondió a la solicitud de autenticación del Player.");

                // No registrar el contenido: puede incluir el token Bearer.
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
                    $"La autenticación del Player excedió el tiempo máximo " +
                    $"de {_apiOptions.TimeoutSeconds} segundos.",
                    ex);
            }
        }

        /// <summary>
        /// Realiza la autenticación RSA del Player mediante desafío y firma digital.
        /// Devuelve el token emitido por la API sin almacenarlo todavía.
        /// </summary>
        /// <param name="numeroSerie">Número de serie del dispositivo.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Token Bearer emitido por la API.</returns>
        public async Task<string> AutenticarConRsaAsync(string numeroSerie, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(numeroSerie);

            // 1. Obtener la clave pública de la identidad local.
            string clavePublica = _deviceIdentityService.GetPublicKey();

            // 2. Solicitar un desafío a la API.
            string jsonDesafio = await SolicitarDesafioAsync(
                numeroSerie,
                clavePublica,
                cancellationToken);

            using JsonDocument documentoDesafio = JsonDocument.Parse(jsonDesafio);

            JsonElement raizDesafio = documentoDesafio.RootElement;

            if (!raizDesafio.TryGetProperty("Status", out JsonElement statusDesafio) ||
                statusDesafio.GetString() != "OK")
            {
                throw new InvalidOperationException("La API no autorizó la solicitud del desafío RSA.");
            }

            JsonElement datosDesafio = raizDesafio.GetProperty("Data");

            string idDesafio = datosDesafio.GetProperty("IdDesafio").GetString()
                ?? throw new InvalidOperationException("La API no devolvió IdDesafio.");

            string desafioBase64 = datosDesafio.GetProperty("Desafio").GetString()
                ?? throw new InvalidOperationException("La API no devolvió Desafio.");

            // 3. Convertir el desafío a bytes y firmarlo con la clave privada.
            byte[] desafio = Convert.FromBase64String(desafioBase64);

            byte[] firma = _deviceIdentityService.SignChallenge(desafio);

            // 4. Enviar la firma para que la API autentique al dispositivo.
            string jsonAutenticacion = await AutenticarPlayerAsync(
                idDesafio,
                Convert.ToBase64String(firma),
                cancellationToken);

            using JsonDocument documentoAutenticacion =
                JsonDocument.Parse(jsonAutenticacion);

            JsonElement raizAutenticacion = documentoAutenticacion.RootElement;

            if (!raizAutenticacion.TryGetProperty("Status", out JsonElement status) ||
                status.GetString() != "OK")
            {
                throw new InvalidOperationException(
                    "La API rechazó la autenticación RSA del Player.");
            }

            JsonElement datosAutenticacion = raizAutenticacion.GetProperty("Data");

            string token = datosAutenticacion.GetProperty("Token").GetString()
                ?? throw new InvalidOperationException(
                    "La API no devolvió el token de autenticación.");

            if (token.Length != 64 ||
                !token.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException(
                    "La API devolvió un token con formato inválido.");
            }

            _logger.LogInformation(
                "Autenticación RSA completada correctamente para el dispositivo {NumeroSerie}.",
                numeroSerie);

            // No registrar el token en logs.
            return token;
        }

        /// <summary>
        /// Comprueba la disponibilidad de la API central mediante
        /// el endpoint público de salud, sin requerir Bearer Token.
        /// </summary>
        /// <param name="cancellationToken">
        /// Token que permite cancelar la operación.
        /// </param>
        /// <returns>
        /// Tarea que finaliza cuando la API responde correctamente.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la URL base no está configurada.
        /// </exception>
        /// <exception cref="HttpRequestException">
        /// Se produce cuando la API no responde correctamente.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Se produce cuando la solicitud supera el tiempo máximo.
        /// </exception>
        public async Task ConsultarSaludAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiOptions.BaseUrl))
            {
                throw new InvalidOperationException(
                    "La URL base de la API no está configurada en Agent:Api:BaseUrl.");
            }

            using HttpClient httpClient = _httpClientFactory.CreateClient();

            httpClient.BaseAddress = new Uri(
                _apiOptions.BaseUrl.TrimEnd('/') + "/");

            httpClient.Timeout = TimeSpan.FromSeconds(
                _apiOptions.TimeoutSeconds);

            try
            {
                using HttpResponseMessage response =
                    await httpClient.GetAsync(
                        "Agent/Health",
                        cancellationToken);

                string content = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al consultar Agent/Health. " +
                        $"Respuesta del servidor: {content}");
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException(
                    $"La consulta de salud de la API excedió el tiempo máximo " +
                    $"de {_apiOptions.TimeoutSeconds} segundos.",
                    ex);
            }
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

            httpClient.BaseAddress = new Uri(_apiOptions.BaseUrl.TrimEnd('/') + "/");

            httpClient.Timeout = TimeSpan.FromSeconds(_apiOptions.TimeoutSeconds);

            string? token = await _playerTokenService.ObtenerTokenAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("El Player no tiene un token dinámico almacenado. " + "Se requiere autenticación RSA.");
            }

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                _logger.LogDebug(
                    "Iniciando solicitud HTTP GET al endpoint {Endpoint}.",
                    endpoint);

                using HttpResponseMessage response = await httpClient.GetAsync(
                    endpoint.TrimStart('/'),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                               
                string content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        bool eliminado = await _playerTokenService.EliminarTokenSiCoincideAsync(token, cancellationToken);

                        if (eliminado)
                        {
                            _logger.LogWarning(
                                "La API rechazó el token dinámico del Player. " +
                                "Se solicitará una nueva autenticación RSA.");
                        }
                    }

                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al consultar '{endpoint}'. " +
                        $"Respuesta del servidor: {content}",
                        inner: null,
                        statusCode: response.StatusCode);
                }

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

            string? token = await _playerTokenService.ObtenerTokenAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("El Player no tiene un token dinámico almacenado. " + "Se requiere autenticación RSA.");
            }

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        bool eliminado = await _playerTokenService
                            .EliminarTokenSiCoincideAsync(token, cancellationToken);

                        if (eliminado)
                        {
                            _logger.LogWarning(
                                "La API rechazó el token dinámico del Player. " +
                                "Se solicitará una nueva autenticación RSA.");
                        }
                    }

                    throw new HttpRequestException(
                        $"Error HTTP {(int)response.StatusCode} al consultar '{endpoint}'. " +
                        $"Respuesta del servidor: {content}",
                        inner: null,
                        statusCode: response.StatusCode);
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
        /// Consulta el estado actual de un comando asignado al Player
        /// autenticado, incluyendo comandos finalizados o expirados.
        /// </summary>
        /// <param name="idComandoPlayer">
        /// Identificador del comando que se desea consultar.
        /// </param>
        /// <param name="cancellationToken">
        /// Token que permite cancelar la solicitud.
        /// </param>
        /// <returns>
        /// Estado del comando correspondiente al Player autenticado.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Se produce cuando el identificador del comando no es válido.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Se produce cuando la API devuelve una respuesta inválida.
        /// </exception>
        public async Task<PlayerComandoEstado> ConsultarEstadoComandoPlayerAsync(int idComandoPlayer, CancellationToken cancellationToken = default)
        {
            if (idComandoPlayer <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(idComandoPlayer),
                    "El identificador del comando debe ser mayor que cero.");
            }

            string endpoint =
                $"Agent/ConsultarEstadoComandoPlayer?id_comando_player={idComandoPlayer}";

            // Reutilizar la comunicación HTTP autenticada existente.
            string json = await GetAsync(endpoint, cancellationToken);

            ApiResponse<PlayerComandoEstado>? response =
                JsonSerializer.Deserialize<ApiResponse<PlayerComandoEstado>>(json);

            if (response == null)
            {
                throw new InvalidOperationException(
                    $"La API devolvió una respuesta nula al consultar el comando {idComandoPlayer}.");
            }

            if (!string.Equals(
                response.Status,
                "OK",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"No fue posible consultar el estado del comando {idComandoPlayer}. " +
                    $"Respuesta de la API: {response.Message}");
            }

            if (response.Data == null)
            {
                throw new InvalidOperationException(
                    $"La API no devolvió información del comando {idComandoPlayer}.");
            }

            if (response.Data.id_comando_player != idComandoPlayer)
            {
                throw new InvalidOperationException(
                    $"La API devolvió un identificador diferente al solicitado. " +
                    $"Solicitado: {idComandoPlayer}. " +
                    $"Recibido: {response.Data.id_comando_player}.");
            }

            _logger.LogDebug(
                "Estado del comando {IdComando} consultado correctamente. " +
                "Estatus: {Estatus}. Expirado: {Expirado}.",
                idComandoPlayer,
                response.Data.codigo_estatus_comando,
                response.Data.expirado);

            return response.Data;
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