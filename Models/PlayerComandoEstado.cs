using System;
using System.Collections.Generic;
using System.Text;


namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa el estado actual de un comando
    /// asignado a un Player específico.
    /// </summary>
    public sealed class PlayerComandoEstado
    {
        public int id_comando_player_detalle { get; set; }

        public int id_comando_player { get; set; }

        public int id_player { get; set; }

        public string codigo_tipo_comando { get; set; } = string.Empty;

        public string codigo_estatus_comando { get; set; } = string.Empty;

        public DateTime fecha_solicitud_utc { get; set; }

        public DateTime fecha_expiracion_utc { get; set; }

        public DateTime? fecha_recepcion_utc { get; set; }

        public DateTime? fecha_finalizacion_utc { get; set; }

        public string? mensaje_resultado { get; set; }

        public bool expirado { get; set; }
    }
}
