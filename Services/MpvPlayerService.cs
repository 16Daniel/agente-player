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
        private string? _currentFile;
        private string? _currentAudioDevice;
        private bool _disposed;

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
        private async Task IniciarProcesoInternoAsync(CancellationToken cancellationToken)
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
            inicio.ArgumentList.Add("--vo=gpu");
            inicio.ArgumentList.Add("--gpu-context=drm");
            inicio.ArgumentList.Add("--hwdec=v4l2m2m-copy");
            inicio.ArgumentList.Add("--ao=alsa");
            inicio.ArgumentList.Add($"--audio-device={_currentAudioDevice}");
            inicio.ArgumentList.Add("--fullscreen");
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
        /// el reproductor y verifica que el dispositivo solicitado
        /// haya quedado seleccionado.
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

                    // Evitar cambios cuando el dispositivo ya está seleccionado.
                    if (string.Equals(
                        _currentAudioDevice,
                        dispositivoAudio,
                        StringComparison.Ordinal))
                    {
                        return;
                    }

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
                    async Task<JsonDocument> EjecutarComandoAsync(string[] argumentos, int requestId)
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
                        // La respuesta exitosa confirma que MPV aceptó el comando.
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
    }
}
