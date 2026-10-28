using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa un comando pendiente a ejecutar por un player Player.
    /// </summary>
    public sealed class PlayerComandoPendiente
    {
        public int id_comando_player { get; set; }

        public string codigo_tipo_comando { get; set; } = string.Empty;

        public string fecha_solicitud_utc { get; set; } = string.Empty;

        public string fecha_expiracion_utc { get; set; } = string.Empty;
    }
}
