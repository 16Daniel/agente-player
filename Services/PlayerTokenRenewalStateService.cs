
using DigitalSignage.Agent.Models;
using System.Text.Json;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Administra el estado persistente de recuperación
    /// de los comandos de renovación del token.
    /// </summary>
    public sealed class PlayerTokenRenewalStateService
    {
        private const string StateFilePath =
            "/var/lib/digital-signage/state/token-renewal.json";

        private readonly SemaphoreSlim _stateLock = new(1, 1);

        /// <summary>
        /// Guarda el estado de recuperación mediante un archivo
        /// temporal y reemplazo atómico.
        /// </summary>
        /// <param name="state">Estado que se desea almacenar.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        public async Task GuardarAsync(
            PlayerTokenRenewalState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (state.IdComandoPlayer <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(state),
                    "El identificador del comando debe ser mayor que cero.");
            }

            await _stateLock.WaitAsync(cancellationToken);

            try
            {
                string directory = Path.GetDirectoryName(StateFilePath)
                    ?? throw new InvalidOperationException(
                        "No fue posible determinar el directorio del estado.");

                Directory.CreateDirectory(directory);

                string temporaryPath = StateFilePath + "." +
                    Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    string json = JsonSerializer.Serialize(state);

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

                    await using (FileStream stream =
                        new FileStream(temporaryPath, options))
                    {
                        await JsonSerializer.SerializeAsync(
                            stream,
                            state,
                            cancellationToken: cancellationToken);

                        await stream.FlushAsync(cancellationToken);
                    }

                    File.Move(
                        temporaryPath,
                        StateFilePath,
                        overwrite: true);

                    if (OperatingSystem.IsLinux())
                    {
                        File.SetUnixFileMode(
                            StateFilePath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Recupera el estado persistente de renovación.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        /// <returns>
        /// Estado almacenado o null cuando todavía no existe.
        /// </returns>
        public async Task<PlayerTokenRenewalState?> ObtenerAsync(
            CancellationToken cancellationToken = default)
        {
            await _stateLock.WaitAsync(cancellationToken);

            try
            {
                if (!File.Exists(StateFilePath))
                {
                    return null;
                }

                if (OperatingSystem.IsLinux())
                {
                    UnixFileMode permisos =
                        File.GetUnixFileMode(StateFilePath);

                    if ((permisos & (UnixFileMode.GroupRead |
                                     UnixFileMode.GroupWrite |
                                     UnixFileMode.GroupExecute |
                                     UnixFileMode.OtherRead |
                                     UnixFileMode.OtherWrite |
                                     UnixFileMode.OtherExecute)) != 0)
                    {
                        throw new UnauthorizedAccessException(
                            "El archivo de recuperación tiene permisos demasiado permisivos.");
                    }
                }

                await using FileStream stream = new(
                    StateFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

                PlayerTokenRenewalState? state =
                    await JsonSerializer.DeserializeAsync<PlayerTokenRenewalState>(
                        stream,
                        cancellationToken: cancellationToken);

                if (state == null ||
                    state.IdComandoPlayer <= 0 ||
                    string.IsNullOrWhiteSpace(state.CodigoEstado))
                {
                    throw new InvalidDataException(
                        "El archivo de recuperación contiene un estado inválido.");
                }

                return state;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "El archivo de recuperación contiene JSON inválido.",
                    ex);
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Elimina el estado local después de confirmar
        /// que el comando tiene un resultado definitivo.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación.</param>
        public async Task EliminarAsync(
            CancellationToken cancellationToken = default)
        {
            await _stateLock.WaitAsync(cancellationToken);

            try
            {
                if (File.Exists(StateFilePath))
                {
                    File.Delete(StateFilePath);
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }
}
