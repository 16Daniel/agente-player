
using DigitalSignage.Agent.Models;
using System.Security.Cryptography;
using System.Text;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Coordina la renovación del token del Player y conserva
    /// información para recuperar operaciones interrumpidas.
    /// </summary>
    public sealed class PlayerTokenRenewalService(
        ApiCommunicationService apiCommunicationService,
        PlayerTokenService playerTokenService,
        PlayerTokenRenewalStateService renewalStateService,
        ILogger<PlayerTokenRenewalService> logger)
    {
        /// <summary>
        /// Renueva el token asociado a un comando específico.
        /// Evita repetir la autenticación cuando identifica
        /// que el token ya fue almacenado.
        /// </summary>
        /// <param name="idComandoPlayer">Identificador del comando.</param>
        /// <param name="numeroSerie">Número de serie del Player.</param>
        /// <param name="cancellationToken">Token de cancelación.</param>
        public async Task RenovarTokenAsync(int idComandoPlayer, string numeroSerie, CancellationToken cancellationToken = default)
        {
            if (idComandoPlayer <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(idComandoPlayer));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(numeroSerie);

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            PlayerTokenRenewalState? estado = await renewalStateService.ObtenerAsync(cancellationToken);

            if (estado != null && estado.CodigoEstado != "Preparado" && estado.CodigoEstado != "TokenGuardado")
            {
                throw new InvalidOperationException(
                    $"El estado de renovación '{estado.CodigoEstado}' " +
                    $"no es válido para el comando {idComandoPlayer}.");
            }

            if (estado != null && estado.IdComandoPlayer != idComandoPlayer)
            {
                throw new InvalidOperationException(
                    $"Existe una renovación pendiente del comando {estado.IdComandoPlayer}. " +
                    $"No se puede iniciar el comando {idComandoPlayer}.");
            }

            if (estado != null && estado.CodigoEstado == "TokenGuardado")
            {
                string? tokenActual = await playerTokenService.ObtenerTokenAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(tokenActual))
                {
                    throw new InvalidOperationException(
                        "El estado indica que el token fue guardado, " +
                        "pero no existe una credencial local.");
                }

                string hashActual = CalcularHash(tokenActual);

                if (string.Equals(
                    hashActual,
                    estado.TokenHash,
                    StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "La renovación del comando {IdComando} ya fue realizada.",
                        idComandoPlayer);

                    return;
                }

                throw new InvalidOperationException(
                    "El token local no coincide con la renovación registrada.");
            }

            bool estadoExistente = estado != null;

            if (estado == null)
            {
                string? tokenAnterior = await playerTokenService.ObtenerTokenAsync(cancellationToken);

                estado = new PlayerTokenRenewalState
                {
                    IdComandoPlayer = idComandoPlayer,
                    CodigoEstado = "Preparado",
                    TokenHashAnterior = string.IsNullOrWhiteSpace(tokenAnterior)
                        ? null
                        : CalcularHash(tokenAnterior),
                    FechaActualizacionUtc = DateTime.UtcNow
                };

                await renewalStateService.GuardarAsync(estado, cancellationToken);
            }

            if (estado.CodigoEstado == "Preparado" && estado.TokenHashAnterior != null)
            {
                string? tokenActual = await playerTokenService.ObtenerTokenAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(tokenActual))
                {
                    string hashActual = CalcularHash(tokenActual);

                    if (!string.Equals(hashActual, estado.TokenHashAnterior, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"El token local cambió durante la renovación del comando " +
                            $"{idComandoPlayer}. Se requiere reconciliar el estado " +
                            "antes de generar otra credencial.");
                    }
                }
            }


            if (estadoExistente && estado.CodigoEstado == "Preparado")
            {
                throw new InvalidOperationException(
                    $"El comando {idComandoPlayer} tiene una renovación " +
                    "interrumpida en estado Preparado. " +
                    "No se repetirá la autenticación RSA automáticamente.");
            }

            string nuevoToken = await apiCommunicationService.AutenticarConRsaAsync(numeroSerie, cancellationToken);

            await playerTokenService.GuardarTokenAsync(nuevoToken, cancellationToken);

            estado.CodigoEstado = "TokenGuardado";
            estado.TokenHash = CalcularHash(nuevoToken);
            estado.FechaActualizacionUtc = DateTime.UtcNow;

            await renewalStateService.GuardarAsync(
                estado,
                cancellationToken);

            logger.LogInformation("Token renovado correctamente para el comando {IdComando}.", idComandoPlayer);
        }

        /// <summary>
        /// Calcula la huella SHA-256 de un token sin registrar
        /// su valor original en archivos de recuperación.
        /// </summary>
        /// <param name="token">Token que se desea identificar.</param>
        /// <returns>Hash SHA-256 hexadecimal.</returns>
        private static string CalcularHash(string token)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));

            return Convert.ToHexString(bytes);
        }
    }
}
