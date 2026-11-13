using System;
using System.Collections.Generic;
using System.Text;


namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa el progreso persistente de una renovación
    /// de token asociada a un comando del Player.
    /// No almacena credenciales de autenticación.
    /// </summary>
    public sealed class PlayerTokenRenewalState
    {
        public int IdComandoPlayer { get; set; }

        public string CodigoEstado { get; set; } = string.Empty;

        public string? TokenHash { get; set; }

        public DateTime FechaActualizacionUtc { get; set; }
        public string? TokenHashAnterior { get; set; }
    }
}
