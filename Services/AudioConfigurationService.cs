using System.Diagnostics;
using System.Text.Json;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Administra la selección y persistencia local de la salida
    /// de audio utilizada por el reproductor MPV.
    /// </summary>
    public sealed class AudioConfigurationService(
        ILogger<AudioConfigurationService> logger)
    {
        // Ubicación del archivo de configuración de audio en el sistema de archivos.
        private const string ConfigurationPath = "/var/lib/digital-signage/state/audio-config.json";

        private readonly SemaphoreSlim _configurationLock = new(1, 1);


        /// <summary>
        /// Guarda localmente la configuración de audio después de que
        /// el reproductor haya aplicado el dispositivo solicitado.
        /// </summary>
        /// <param name="idAudio">1: HDMI; 2: Jack 3.5 mm.</param>
        /// <param name="versionConfiguracion">Versión de configuración recibida desde la API.</param>
        /// <param name="dispositivo">Dispositivo ALSA aplicado en MPV.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Tarea que representa la operación de almacenamiento.</returns>
        public async Task GuardarConfiguracionAsync(int idAudio, int versionConfiguracion, string dispositivo, CancellationToken cancellationToken = default)
        {
            if (idAudio != 1 && idAudio != 2)
                throw new ArgumentOutOfRangeException(nameof(idAudio));

            if (versionConfiguracion <= 0)
                throw new ArgumentOutOfRangeException(nameof(versionConfiguracion));

            ArgumentException.ThrowIfNullOrWhiteSpace(dispositivo);

            await _configurationLock.WaitAsync(cancellationToken);

            try
            {
                string directorio = Path.GetDirectoryName(ConfigurationPath)!;
                Directory.CreateDirectory(directorio);

                string temporal = ConfigurationPath + "." +
                    Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    string json = JsonSerializer.Serialize(new
                    {
                        IdAudio = idAudio,
                        AudioDevice = dispositivo,
                        VersionConfiguracion = versionConfiguracion
                    });

                    await File.WriteAllTextAsync(
                        temporal,
                        json,
                        cancellationToken);

                    if (OperatingSystem.IsLinux())
                    {
                        File.SetUnixFileMode(
                            temporal,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    File.Move(temporal, ConfigurationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporal))
                        File.Delete(temporal);
                }

                logger.LogInformation(
                    "Configuración de audio guardada. Salida: {Dispositivo}. Versión: {Version}.",
                    dispositivo,
                    versionConfiguracion);
            }
            finally
            {
                _configurationLock.Release();
            }
        }


        /// <summary>
        /// Recupera la configuración de audio almacenada localmente
        /// para reutilizarla después de reiniciar el Agent.
        /// </summary>
        /// <param name="cancellationToken">
        /// Token de cancelación de la operación.
        /// </param>
        /// <returns>
        /// Dispositivo ALSA configurado o null si no existe
        /// una configuración de audio local.
        /// </returns>
        public async Task<string?> ObtenerDispositivoConfiguradoAsync(CancellationToken cancellationToken = default)
        {
            await _configurationLock.WaitAsync(cancellationToken);

            try
            {
                if (!File.Exists(ConfigurationPath))
                {
                    logger.LogWarning(
                        "No existe una configuración de audio local.");

                    return null;
                }

                string json = await File.ReadAllTextAsync(
                    ConfigurationPath,
                    cancellationToken);

                using JsonDocument documento = JsonDocument.Parse(json);

                JsonElement raiz = documento.RootElement;

                int idAudio = raiz.GetProperty("IdAudio").GetInt32();

                string? dispositivo = raiz
                    .GetProperty("AudioDevice")
                    .GetString();

                int version = raiz
                    .GetProperty("VersionConfiguracion")
                    .GetInt32();

                if ((idAudio != 1 && idAudio != 2) ||
                    string.IsNullOrWhiteSpace(dispositivo) ||
                    version <= 0)
                {
                    throw new InvalidOperationException(
                        "La configuración de audio local contiene valores inválidos.");
                }

                string salida = idAudio == 1
                    ? "HDMI"
                    : "Jack 3.5 mm";

                logger.LogInformation(
                    "Configuración de audio recuperada. Salida: {Salida}. Versión: {Version}.",
                    salida,
                    version);

                return dispositivo;
            }
            finally
            {
                _configurationLock.Release();
            }
        }


        /// <summary>
        /// Detecta el dispositivo ALSA correspondiente a la salida solicitada,
        /// sin modificar la configuración almacenada localmente.
        /// </summary>
        /// <param name="idAudio">1: HDMI; 2: Jack 3.5 mm.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Identificador ALSA detectado.</returns>
        public async Task<string> ObtenerDispositivoAsync(
            int idAudio,
            CancellationToken cancellationToken = default)
        {
            if (idAudio != 1 && idAudio != 2)
                throw new ArgumentOutOfRangeException(nameof(idAudio));

            return await DetectarDispositivoAsync(idAudio, cancellationToken);
        }


        /// <summary>
        /// Consulta las salidas disponibles en MPV y selecciona
        /// la correspondiente al tipo de audio solicitado.
        /// </summary>
        private static async Task<string> DetectarDispositivoAsync(int idAudio, CancellationToken cancellationToken)
        {
            using Process proceso = new();

            proceso.StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/mpv",
                Arguments = "--no-config --ao=alsa --audio-device=help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            proceso.Start();

            Task<string> salidaTask = proceso.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = proceso.StandardError.ReadToEndAsync(cancellationToken);

            await proceso.WaitForExitAsync(cancellationToken);

            string salida = await salidaTask;
            string errores = await errorTask;

            if (proceso.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"No fue posible consultar los dispositivos de audio: {errores}");
            }

            string[] dispositivos = salida.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries);

            string? dispositivo = dispositivos
                .Select(linea => linea.Trim())
                .Where(linea => linea.StartsWith("'alsa/", StringComparison.OrdinalIgnoreCase))
                .Select(linea => linea.Split('\'')[1])
                .FirstOrDefault(nombre =>
                    idAudio == 1
                        ? nombre.Contains("hdmi", StringComparison.OrdinalIgnoreCase)
                        : nombre.Contains("Headphones", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(dispositivo))
            {
                throw new InvalidOperationException(
                    $"No se encontró una salida ALSA compatible con id_audio={idAudio}.");
            }

            return dispositivo;
        }
    }
}