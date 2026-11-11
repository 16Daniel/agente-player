
using System;

namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa el estado local de aprovisionamiento
    /// de la identidad criptográfica del dispositivo.
    /// </summary>
    public sealed class DeviceIdentityState
    {
        /// <summary>
        /// Indica si la identidad fue generada localmente.
        /// No implica autorización por parte de la API.
        /// </summary>
        public bool IdentityGenerated { get; set; }

        /// <summary>
        /// Indica si la API confirmó previamente la vinculación.
        /// Es información local, no una prueba de autenticación.
        /// </summary>
        public bool WasLinked { get; set; }

        /// <summary>
        /// Fecha UTC de generación de la identidad.
        /// </summary>
        public DateTime? CreatedAtUtc { get; set; }

    }
}
