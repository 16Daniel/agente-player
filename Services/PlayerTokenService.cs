
using System.Text.Json;
using System.Text;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Administra el almacenamiento local y la recuperación
    /// del token dinámico de autenticación del Player.
    /// </summary>
    public sealed class PlayerTokenService
    {
        private const string TokenFilePath =
            "/var/lib/digital-signage/state/player-token.json";

        private readonly ILogger<PlayerTokenService> _logger;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);

        /// <summary>
        /// Inicializa el servicio responsable de administrar
        /// el token de autenticación del dispositivo.
        /// </summary>
        /// <param name="logger">Servicio de registro de eventos.</param>
        public PlayerTokenService(ILogger<PlayerTokenService> logger)
        {
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Almacena de forma persistente el token dinámico del Player,
        /// utilizando permisos restrictivos y un archivo temporal.
        /// </summary>
        /// <param name="token">Token Bearer generado por la API.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Tarea que representa la operación de almacenamiento.</returns>
        public async Task GuardarTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);

            if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("El token recibido no tiene el formato hexadecimal esperado.", nameof(token));
            }

            // Evita que otra operación modifique el token simultáneamente.
            await _tokenLock.WaitAsync(cancellationToken);

            try
            {
                string directory = Path.GetDirectoryName(TokenFilePath) ?? throw new InvalidOperationException("No fue posible determinar el directorio del token.");

                Directory.CreateDirectory(directory);

                string temporaryPath = TokenFilePath + "." +
                    Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    string json = JsonSerializer.Serialize(new
                    {
                        Token = token
                    });

                    byte[] content = Encoding.UTF8.GetBytes(json);

                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous,
                        UnixCreateMode = OperatingSystem.IsLinux()
                            ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                            : null
                    };

                    await using (FileStream stream = new FileStream(
                        temporaryPath,
                        options))
                    {
                        await stream.WriteAsync(content, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }

                    // Reemplazar el archivo definitivo en el mismo directorio.
                    File.Move(temporaryPath, TokenFilePath, overwrite: true);

                    if (OperatingSystem.IsLinux())
                    {
                        File.SetUnixFileMode(
                            TokenFilePath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    _logger.LogInformation("Token dinámico del Player almacenado correctamente.");
                }
                finally
                {
                    // Eliminar el archivo temporal si todavía existe.
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            finally
            {
                // Liberar el bloqueo incluso si ocurre una excepción.
                _tokenLock.Release();
            }
        }


        /// <summary>
        /// Recupera el token dinámico almacenado localmente.
        /// Devuelve null cuando todavía no existe un token.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>
        /// Token Bearer almacenado o null si no existe.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// El archivo existe, pero su contenido es inválido.
        /// </exception>
        public async Task<string?> ObtenerTokenAsync( CancellationToken cancellationToken = default)
        {
            if (!File.Exists(TokenFilePath))
            {
                return null;
            }

            // Verificar que el archivo mantenga permisos restrictivos.
            if (OperatingSystem.IsLinux())
            {
                UnixFileMode permisos = File.GetUnixFileMode(TokenFilePath);

                if ((permisos & (UnixFileMode.GroupRead |
                                UnixFileMode.GroupWrite |
                                UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead |
                                UnixFileMode.OtherWrite |
                                UnixFileMode.OtherExecute)) != 0)
                {
                    throw new UnauthorizedAccessException("El archivo del token tiene permisos demasiado permisivos.");
                }
            }

            try
            {
                string json = await File.ReadAllTextAsync(TokenFilePath, cancellationToken);

                using JsonDocument documento = JsonDocument.Parse(json);

                if (!documento.RootElement.TryGetProperty("Token", out JsonElement tokenElement))
                {
                    throw new InvalidDataException("El archivo del token no contiene la propiedad Token.");
                }

                string? token = tokenElement.GetString();

                if (string.IsNullOrWhiteSpace(token) || token.Length != 64 || !token.All(Uri.IsHexDigit))
                {
                    throw new InvalidDataException("El token almacenado tiene un formato inválido.");
                }

                return token;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("El archivo del token contiene un JSON inválido.", ex);
            }
        }

        /// <summary>
        /// Elimina el token dinámico almacenado localmente.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>Tarea que representa la operación.</returns>
        public async Task EliminarTokenAsync(CancellationToken cancellationToken = default)
        {
            await _tokenLock.WaitAsync(cancellationToken);

            try
            {
                if (File.Exists(TokenFilePath))
                {
                    File.Delete(TokenFilePath);

                    _logger.LogInformation(
                        "Token dinámico local eliminado.");
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// Elimina el token local únicamente cuando coincide con la
        /// credencial que fue rechazada por la API.
        /// </summary>
        /// <param name="tokenRechazado">
        /// Token utilizado en la solicitud HTTP rechazada.
        /// </param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>
        /// True si se eliminó el token; false si no existe o fue reemplazado.
        /// </returns>
        public async Task<bool> EliminarTokenSiCoincideAsync(string tokenRechazado, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tokenRechazado);

            await _tokenLock.WaitAsync(cancellationToken);

            try
            {
                string? tokenActual = await ObtenerTokenAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(tokenActual))
                {
                    return false;
                }

                if (!string.Equals(tokenActual, tokenRechazado, StringComparison.Ordinal))
                {
                    return false;
                }

                File.Delete(TokenFilePath);

                _logger.LogWarning("Token dinámico rechazado por la API y eliminado localmente.");

                return true;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

    }
}
