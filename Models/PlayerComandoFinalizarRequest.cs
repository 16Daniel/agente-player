using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa la solicitud para finalizar un comando del Player.
    /// </summary>
    public sealed class PlayerComandoFinalizarRequest
    {
        public int id_comando_player { get; set; }

        public string codigo_resultado { get; set; } = string.Empty;

        public string mensaje_resultado { get; set; } = string.Empty;
    }
}
