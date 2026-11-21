using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Controla MPV, su salida de audio y la recuperación de la reproducción local.
    /// </summary>
    public sealed class MpvPlayerService : IAsyncDisposable
    {
        private readonly ILogger<MpvPlayerService> _logger;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private readonly SemaphoreSlim _processLock = new(1, 1);

        private Process? _process;
        private Task? _errorReaderTask;
        private CancellationTokenSource? _monitorCancellation;
        private Task? _monitorTask;
        private Task? _hdmiMonitorTask;
        private string? _currentFile;
        private string? _currentAudioDevice;
        private bool _disposed;
        private bool _headless;

        /// <summary>Inicializa el controlador MPV.</summary>
        /// <param name="logger">Registro de eventos.</param>
        public MpvPlayerService(ILogger<MpvPlayerService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Inicia o cambia la reproducción. Si el archivo y el audio no cambian,
        /// mantiene el proceso actual o su recuperación en curso.
        /// </summary>
        /// <param name="archivo">Ruta absoluta del archivo local.</param>
        /// <param name="dispositivoAudio">Dispositivo ALSA detectado.</param>
        /// <param name="cancellationToken">Cancelación de la solicitud.</param>
        public async Task IniciarAsync(string archivo, string dispositivoAudio, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(archivo);
            ArgumentException.ThrowIfNullOrWhiteSpace(dispositivoAudio);

            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (!File.Exists(archivo))
                    throw new FileNotFoundException("El archivo multimedia no existe.", archivo);

                if (_monitorTask is { IsCompleted: false } &&
                    string.Equals(_currentFile, archivo, StringComparison.Ordinal) &&
                    string.Equals(_currentAudioDevice, dispositivoAudio, StringComparison.Ordinal))
                    return;

                await DetenerSesionAsync();
                cancellationToken.ThrowIfCancellationRequested();

                await _processLock.WaitAsync(cancellationToken);
                try
                {
                    _currentFile = archivo;
                    _currentAudioDevice = dispositivoAudio;

                    try
                    {
                        await IniciarProcesoInternoAsync(cancellationToken);
                    }
                    catch
                    {
                        _currentFile = null;
                        _currentAudioDevice = null;
                        throw;
                    }

                    _monitorCancellation = new CancellationTokenSource();
                    _monitorTask = SupervisarAsync(_monitorCancellation.Token);
                    _hdmiMonitorTask = SupervisarHdmiAsync(_monitorCancellation.Token);
                }
                finally
                {
                    _processLock.Release();
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>Inicia MPV con los parámetros comprobados en Raspberry Pi.</summary>
        private async Task IniciarProcesoInternoAsync(CancellationToken cancellationToken, double? posicionInicial = null)
        {
            if (_currentFile == null || _currentAudioDevice == null)
                throw new InvalidOperationException("No existe una configuración de reproducción activa.");

            if (!File.Exists(_currentFile))
                throw new FileNotFoundException("El archivo local dejó de estar disponible.", _currentFile);

            var inicio = new ProcessStartInfo
            {
                FileName = "/usr/bin/mpv",
                WorkingDirectory = "/var/lib/digital-signage",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            inicio.ArgumentList.Add("--no-config");
            inicio.ArgumentList.Add("--no-terminal");
            inicio.ArgumentList.Add("--msg-level=all=warn");
            inicio.ArgumentList.Add("--input-ipc-server=/run/digital-signage/mpv.sock");
            // Sin pantalla, MPV mantiene la línea de tiempo sin requerir DRM.
            // Con pantalla se conservan los parámetros de video ya validados.
            bool sinPantalla = LeerConexionHdmi() == false;
            if (sinPantalla)
            {
                inicio.ArgumentList.Add("--vo=null");
                inicio.ArgumentList.Add("--hwdec=no");
                if (_currentAudioDevice.Contains("hdmi", StringComparison.OrdinalIgnoreCase))
                {
                    inicio.ArgumentList.Add("--ao=null");
                }
                else
                {
                    inicio.ArgumentList.Add("--ao=alsa");
                    inicio.ArgumentList.Add($"--audio-device={_currentAudioDevice}");
                }
            }
            else
            {
                inicio.ArgumentList.Add("--vo=gpu");
                inicio.ArgumentList.Add("--gpu-context=drm");
                inicio.ArgumentList.Add("--hwdec=v4l2m2m-copy");
                inicio.ArgumentList.Add("--ao=alsa");
                inicio.ArgumentList.Add($"--audio-device={_currentAudioDevice}");
                inicio.ArgumentList.Add("--fullscreen");
            }

            if (posicionInicial is >= 0)
                inicio.ArgumentList.Add($"--start={posicionInicial.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
            inicio.ArgumentList.Add("--loop-file=inf");
            inicio.ArgumentList.Add(_currentFile);

            var proceso = new Process { StartInfo = inicio };
            Task? lector = null;
            bool iniciado = false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!proceso.Start())
                    throw new InvalidOperationException("No fue posible iniciar MPV.");

                iniciado = true;
                lector = RegistrarErroresAsync(proceso);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                if (proceso.HasExited)
                    throw new InvalidOperationException(
                        $"MPV terminó durante el inicio. Código: {proceso.ExitCode}.");

                _process = proceso;
                _errorReaderTask = lector;
                _headless = sinPantalla;

                _logger.LogInformation(
                    "MPV iniciado. PID: {Pid}. Archivo: {Archivo}. Audio: {Audio}.",
                    proceso.Id, _currentFile, _currentAudioDevice);
            }
            catch
            {
                try
                {
                    if (iniciado && !proceso.HasExited)
                        proceso.Kill(entireProcessTree: true);

                    if (iniciado)
                        await proceso.WaitForExitAsync(CancellationToken.None);
                }
                finally
                {
                    if (lector != null)
                        await lector;
                    proceso.Dispose();
                }
                throw;
            }
        }

        /// <summary>Supervisa MPV y reintenta iniciarlo tras una falla.</summary>
        private async Task SupervisarAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Process? proceso;
                    await _processLock.WaitAsync(cancellationToken);
                    try { proceso = _process; }
                    finally { _processLock.Release(); }

                    if (proceso != null)
                    {
                        await proceso.WaitForExitAsync(cancellationToken);
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        _logger.LogWarning("MPV terminó inesperadamente. Código: {Codigo}.",
                            proceso.ExitCode);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

                    await _processLock.WaitAsync(cancellationToken);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        // Una transición HDMI controlada pudo sustituir el proceso.
                        if (proceso != null && !ReferenceEquals(_process, proceso))
                            continue;

                        await DetenerProcesoInternoAsync();
                        await IniciarProcesoInternoAsync(cancellationToken);
                    }
                    finally
                    {
                        _processLock.Release();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falló MPV; se intentará recuperar la reproducción.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Supervisa periódicamente la conexión HDMI y recupera la pista
        /// de audio cuando MPV la desactiva, incluso sin desconexión física.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        private async Task SupervisarHdmiAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool? conectado = LeerConexionHdmi();
                    bool transicion = false;

                    // Solo se cambia de modo cuando MPV está en modo sin pantalla
                    // y aparece un HDMI. Si DRM sigue funcionando al desconectar,
                    // no se toca el proceso: preservamos el hotplug ya validado.
                    if (conectado == true)
                    {
                        await _processLock.WaitAsync(cancellationToken);
                        try
                        {
                            if (_headless && _process is { HasExited: false })
                            {
                                double? posicion = await ConsultarPosicionAsync(cancellationToken);
                                if (posicion.HasValue)
                                {
                                    await DetenerProcesoInternoAsync();
                                    await IniciarProcesoInternoAsync(cancellationToken, posicion.Value);
                                    transicion = true;
                                    _logger.LogInformation(
                                        "Salida HDMI restaurada. Reproducción retomada en {Segundos:F2} segundos.",
                                        posicion.Value);
                                }
                            }
                        }
                        finally
                        {
                            _processLock.Release();
                        }

                        // El método ya validado obtiene su propio bloqueo.
                        await RecuperarAudioAsync(cancellationToken);
                    }
                    else
                    {
                        // Jack debe recuperarse incluso si no existe pantalla conectada.
                        await RecuperarAudioAsync(cancellationToken);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(transicion ? 1 : 2), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error al supervisar el HDMI; se reintentará.");
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }

        /// <summary>
        /// Consulta la posición actual de MPV mediante IPC antes de cambiar
        /// del modo sin pantalla a DRM. Devuelve null para reintentar sin perder posición.
        /// </summary>
        /// <param name="cancellationToken">Cancelación de la consulta.</param>
        /// <returns>Posición en segundos, o null si no pudo obtenerse.</returns>
        private async Task<double?> ConsultarPosicionAsync(CancellationToken cancellationToken)
        {
            const string ruta = "/run/digital-signage/mpv.sock";
            if (!File.Exists(ruta))
                return null;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(ruta), timeout.Token);
                using var stream = new NetworkStream(socket, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                byte[] comando = Encoding.UTF8.GetBytes(
                    "{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":901}\n");
                await stream.WriteAsync(comando, timeout.Token);
                await stream.FlushAsync(timeout.Token);

                while (true)
                {
                    string? linea = await reader.ReadLineAsync(timeout.Token);
                    if (linea == null)
                        return null;

                    using JsonDocument respuesta = JsonDocument.Parse(linea);
                    JsonElement raiz = respuesta.RootElement;
                    if (!raiz.TryGetProperty("request_id", out JsonElement id) ||
                        !id.TryGetInt32(out int recibido) || recibido != 901)
                        continue;

                    if (!raiz.TryGetProperty("error", out JsonElement error) ||
                        error.GetString() != "success" ||
                        !raiz.TryGetProperty("data", out JsonElement dato) ||
                        !dato.TryGetDouble(out double posicion) ||
                        !double.IsFinite(posicion) || posicion < 0)
                        return null;

                    return posicion;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Tiempo de espera agotado al consultar posición de MPV.");
                return null;
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "No fue posible consultar posición de MPV; se reintentará.");
                return null;
            }
        }

        /// <summary>
        /// Lee el estado de los conectores DRM HDMI. Devuelve null si el sistema no expone
        /// ningún conector, para evitar interpretar una lectura fallida como desconexión.
        /// </summary>
        private static bool? LeerConexionHdmi()
        {
            if (!Directory.Exists("/sys/class/drm"))
                return null;

            string[] conectores = Directory.GetDirectories("/sys/class/drm", "*HDMI*");
            bool encontrado = false;

            foreach (string conector in conectores)
            {
                string ruta = Path.Combine(conector, "status");
                if (!File.Exists(ruta))
                    continue;

                encontrado = true;
                if (string.Equals(File.ReadAllText(ruta).Trim(), "connected", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return encontrado ? false : null;
        }

        /// <summary>
        /// Recupera por IPC la pista de audio seleccionada tanto para Jack como HDMI.
        /// No intenta habilitar HDMI cuando el conector está desconectado.
        /// </summary>
        /// <returns>True si no se requiere más recuperación; false si debe reintentarse.</returns>
        private async Task<bool> RecuperarAudioAsync(CancellationToken cancellationToken)
        {
            await _processLock.WaitAsync(cancellationToken);
            try
            {
                if (_process == null || _process.HasExited || _currentAudioDevice == null)
                    return true;

                // No intentar activar audio HDMI mientras no hay pantalla.
                if (_currentAudioDevice.Contains("hdmi", StringComparison.OrdinalIgnoreCase) &&
                    LeerConexionHdmi() != true)
                    return true;

                const string ruta = "/run/digital-signage/mpv.sock";
                if (!File.Exists(ruta))
                    return false;

                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(ruta), timeout.Token);
                using var stream = new NetworkStream(socket, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                async Task<JsonDocument> EnviarAsync(object[] argumentos, int id)
                {
                    string json = JsonSerializer.Serialize(new { command = argumentos, request_id = id });
                    byte[] datos = Encoding.UTF8.GetBytes(json + "\n");
                    await stream.WriteAsync(datos, timeout.Token);
                    await stream.FlushAsync(timeout.Token);

                    while (true)
                    {
                        string? linea = await reader.ReadLineAsync(timeout.Token);
                        if (linea == null)
                            throw new IOException("MPV cerró el socket IPC durante la recuperación de audio.");

                        using JsonDocument respuesta = JsonDocument.Parse(linea);
                        JsonElement raiz = respuesta.RootElement;
                        if (!raiz.TryGetProperty("request_id", out JsonElement identificador) ||
                            !identificador.TryGetInt32(out int recibido) || recibido != id)
                            continue;

                        if (!raiz.TryGetProperty("error", out JsonElement error) ||
                            error.GetString() != "success")
                            throw new InvalidOperationException($"MPV rechazó IPC {id}: {linea}");

                        return JsonDocument.Parse(linea);
                    }
                }

                using JsonDocument pista = await EnviarAsync(new object[] { "get_property", "aid" }, 101);
                JsonElement aid = pista.RootElement.GetProperty("data");
                if (aid.ValueKind == JsonValueKind.Number && aid.TryGetInt32(out int actual) && actual > 0)
                    return true;

                using JsonDocument pistas = await EnviarAsync(new object[] { "get_property", "track-list" }, 102);
                int? idAudio = null;
                foreach (JsonElement elemento in pistas.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (elemento.TryGetProperty("type", out JsonElement tipo) &&
                        tipo.GetString() == "audio" &&
                        elemento.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int valor))
                    {
                        idAudio = valor;
                        break;
                    }
                }

                if (idAudio == null)
                {
                    _logger.LogWarning("No se encontró pista de audio para recuperar la salida seleccionada.");
                    return false;
                }

                using JsonDocument cambio = await EnviarAsync(new object[] { "set_property", "aid", idAudio.Value }, 103);
                using JsonDocument comprobacion = await EnviarAsync(new object[] { "get_property", "aid" }, 104);
                JsonElement seleccionado = comprobacion.RootElement.GetProperty("data");
                bool recuperado = seleccionado.ValueKind == JsonValueKind.Number &&
                    seleccionado.TryGetInt32(out int idSeleccionado) && idSeleccionado == idAudio.Value;

                if (recuperado)
                    _logger.LogInformation("Pista de audio recuperada por IPC. Dispositivo: {Dispositivo}. Pista: {Pista}.", _currentAudioDevice, idAudio.Value);

                return recuperado;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Tiempo de espera agotado durante recuperación de audio.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or JsonException)
            {
                _logger.LogWarning(ex, "No fue posible recuperar la pista de audio; se reintentará.");
                return false;
            }
            finally
            {
                _processLock.Release();
            }
        }

        /// <summary>Registra la salida de diagnóstico de MPV.</summary>
        private async Task RegistrarErroresAsync(Process proceso)
        {
            try
            {
                string? linea;
                while ((linea = await proceso.StandardError.ReadLineAsync()) != null)
                    _logger.LogWarning("MPV: {Mensaje}", linea);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No fue posible leer el diagnóstico de MPV.");
            }
        }

        /// <summary>Detiene MPV y cancela su recuperación automática.</summary>
        /// <param name="cancellationToken">Cancelación de la solicitud.</param>
        public async Task DetenerAsync(CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                await DetenerSesionAsync();
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Detiene una sesión completa sin competir con el monitor.
        /// Se ejecuta exclusivamente bajo el bloqueo de operaciones.
        /// </summary>
        private async Task DetenerSesionAsync()
        {
            if (_monitorCancellation != null)
                await _monitorCancellation.CancelAsync();

            // Esperar fuera del bloqueo del proceso: el monitor también lo utiliza.
            if (_monitorTask != null)
                await _monitorTask;

            if (_hdmiMonitorTask != null)
                await _hdmiMonitorTask;

            await _processLock.WaitAsync();
            try
            {
                await DetenerProcesoInternoAsync();
                _currentFile = null;
                _currentAudioDevice = null;
            }
            finally
            {
                _processLock.Release();
            }

            _monitorTask = null;
            _hdmiMonitorTask = null;
            _monitorCancellation?.Dispose();
            _monitorCancellation = null;
        }

        /// <summary>Finaliza y libera el proceso MPV actual.</summary>
        private async Task DetenerProcesoInternoAsync()
        {
            Process? proceso = _process;
            Task? lector = _errorReaderTask;
            _process = null;
            _errorReaderTask = null;

            if (proceso == null)
                return;

            try
            {
                if (!proceso.HasExited)
                    proceso.Kill(entireProcessTree: true);

                await proceso.WaitForExitAsync(CancellationToken.None);
                if (lector != null)
                    await lector;
            }
            catch (InvalidOperationException)
            {
                // El proceso ya había finalizado.
            }
            finally
            {
                proceso.Dispose();
            }
        }

        /// <summary>Detiene la reproducción y libera los recursos del controlador.</summary>
        public async ValueTask DisposeAsync()
        {
            await _operationLock.WaitAsync();
            try
            {
                if (_disposed)
                    return;

                _disposed = true;
                await DetenerSesionAsync();
            }
            finally
            {
                _operationLock.Release();
            }
            // No liberar los semáforos: una llamada concurrente podría estar esperándolos.
        }

        /// <summary>
        /// Cambia la salida de audio de MPV mediante IPC sin reiniciar
        /// el reproductor, verifica el dispositivo seleccionado e intenta
        /// recuperar la pista de audio para HDMI o Jack.
        /// </summary>
        /// <param name="dispositivoAudio">Dispositivo ALSA solicitado.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Tarea que representa la operación.</returns>
        public async Task CambiarAudioAsync(string dispositivoAudio, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dispositivoAudio);

            const string socketPath = "/run/digital-signage/mpv.sock";

            await _operationLock.WaitAsync(cancellationToken);

            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                await _processLock.WaitAsync(cancellationToken);

                try
                {
                    if (string.IsNullOrWhiteSpace(_currentFile) ||
                        _process == null ||
                        _process.HasExited ||
                        !File.Exists(socketPath))
                    {
                        throw new InvalidOperationException(
                            "MPV no está disponible para cambiar el audio mediante IPC.");
                    }

                    // Una solicitud repetida no debe reiniciar MPV ni reconfigurar ALSA.
                    // La recuperación de aid se realizará fuera del bloqueo.
                    bool mismoDispositivo = string.Equals(
                        _currentAudioDevice, dispositivoAudio, StringComparison.Ordinal);

                    if (!mismoDispositivo)
                    {
                        // En modo sin pantalla el dispositivo de audio puede requerir
                        // cambiar también --ao (null/alsa). Se reinicia preservando
                        // la posición; con HDMI se conserva el IPC ya validado.
                        if (_headless)
                        {
                            double? posicion = await ConsultarPosicionAsync(cancellationToken);
                            if (!posicion.HasValue)
                                throw new InvalidOperationException(
                                    "No se pudo consultar la posición de MPV para cambiar audio sin pantalla.");

                            string? audioAnterior = _currentAudioDevice;
                            _currentAudioDevice = dispositivoAudio;
                            try
                            {
                                await DetenerProcesoInternoAsync();
                                await IniciarProcesoInternoAsync(cancellationToken, posicion.Value);
                            }
                            catch
                            {
                                _currentAudioDevice = audioAnterior;
                                throw;
                            }

                            _logger.LogInformation(
                                "Audio actualizado sin pantalla: {Dispositivo}. Posición: {Segundos:F2} segundos.",
                                dispositivoAudio, posicion.Value);
                        }
                        else
                        {

                            using var socket = new Socket(
                                AddressFamily.Unix,
                                SocketType.Stream,
                                ProtocolType.Unspecified);

                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken);

                            timeout.CancelAfter(TimeSpan.FromSeconds(5));

                            await socket.ConnectAsync(
                                new UnixDomainSocketEndPoint(socketPath),
                                timeout.Token);

                            using var stream = new NetworkStream(
                                socket,
                                ownsSocket: true);

                            using var reader = new StreamReader(
                                stream,
                                Encoding.UTF8,
                                leaveOpen: true);

                            // Envía un comando y espera exclusivamente su respuesta.
                            async Task<JsonDocument> EjecutarComandoAsync(
                                string[] argumentos,
                                int requestId)
                            {
                                string comando = JsonSerializer.Serialize(new
                                {
                                    command = argumentos,
                                    request_id = requestId
                                });

                                byte[] datos = Encoding.UTF8.GetBytes(comando + "\n");

                                await stream.WriteAsync(datos, timeout.Token);
                                await stream.FlushAsync(timeout.Token);

                                while (true)
                                {
                                    string? respuesta =
                                        await reader.ReadLineAsync(timeout.Token);

                                    if (respuesta == null)
                                    {
                                        throw new InvalidOperationException(
                                            $"MPV cerró la conexión IPC. Comando: {requestId}.");
                                    }

                                    if (string.IsNullOrWhiteSpace(respuesta))
                                        continue;

                                    JsonDocument json = JsonDocument.Parse(respuesta);

                                    JsonElement raiz = json.RootElement;

                                    // Ignorar eventos y respuestas de otros comandos.
                                    if (!raiz.TryGetProperty(
                                            "request_id",
                                            out JsonElement identificador) ||
                                        !identificador.TryGetInt32(out int idRecibido) ||
                                        idRecibido != requestId)
                                    {
                                        json.Dispose();
                                        continue;
                                    }

                                    if (!raiz.TryGetProperty(
                                            "error",
                                            out JsonElement errorElement) ||
                                        errorElement.ValueKind != JsonValueKind.String ||
                                        !string.Equals(
                                            errorElement.GetString(),
                                            "success",
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        json.Dispose();

                                        throw new InvalidOperationException(
                                            $"MPV rechazó el comando IPC {requestId}. " +
                                            $"Respuesta: {respuesta}");
                                    }

                                    return json;
                                }
                            }

                            // Aplicar la nueva salida de audio.
                            using (JsonDocument cambio = await EjecutarComandoAsync(
                                new[]
                                {
                            "set_property",
                            "audio-device",
                            dispositivoAudio
                                },
                                1))
                            {
                                // MPV aceptó el cambio de dispositivo.
                            }

                            // Consultar la salida seleccionada.
                            using JsonDocument verificacion = await EjecutarComandoAsync(
                                new[]
                                {
                            "get_property",
                            "audio-device"
                                },
                                2);

                            if (!verificacion.RootElement.TryGetProperty(
                                    "data",
                                    out JsonElement dato) ||
                                dato.ValueKind != JsonValueKind.String)
                            {
                                throw new InvalidOperationException(
                                    "MPV no devolvió el dispositivo de audio seleccionado.");
                            }

                            string? dispositivoAplicado = dato.GetString();

                            if (!string.Equals(
                                dispositivoAplicado,
                                dispositivoAudio,
                                StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(
                                    $"MPV no confirmó la salida solicitada. " +
                                    $"Solicitada: {dispositivoAudio}. " +
                                    $"Actual: {dispositivoAplicado}.");
                            }

                            // Actualizar el estado interno únicamente después de verificar.
                            _currentAudioDevice = dispositivoAudio;

                            _logger.LogInformation(
                                "Salida de audio modificada mediante IPC: {Dispositivo}.",
                                dispositivoAudio);
                        } // Fin de la ruta IPC con pantalla.
                    } // Fin de cambio real de dispositivo.
                }
                finally
                {
                    _processLock.Release();
                }

                // Recuperar la pista seleccionada sin mantener _processLock.
                bool recuperado = await RecuperarAudioAsync(cancellationToken);
                if (!recuperado)
                    _logger.LogWarning("No se recuperó inmediatamente la pista de audio; el monitor reintentará.");
            }
            finally
            {
                _operationLock.Release();
            }
        }
    }
}
