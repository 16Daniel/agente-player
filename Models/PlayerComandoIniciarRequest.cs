using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa la solicitud para iniciar un comando asignado al Player. Esto cambiará su estado en SQL Server de PENDIENTE a EN_PROCESO 
    /// y registrará fecha_recepcion_utc.
    /// </summary>
    public sealed class PlayerComandoIniciarRequest
    {
        public int id_comando_player { get; set; }
    }
}
