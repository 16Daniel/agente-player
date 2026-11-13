using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Mantiene el último estado administrativo confirmado del Player.
    /// Por seguridad, las operaciones permanecen suspendidas
    /// hasta que la API confirme que está activo.
    /// </summary>
    public sealed class PlayerAdministrativeState
    {
        private int _activo;

        /// <summary>
        /// Indica si la API confirmó que el Player está activo.
        /// </summary>
        public bool EstaActivo => Volatile.Read(ref _activo) == 1;

        /// <summary>
        /// Actualiza el permiso de operación del Player.
        /// </summary>
        /// <param name="activo">True cuando el Player está activo.</param>
        public void EstablecerEstado(bool activo)
        {
            Volatile.Write(ref _activo, activo ? 1 : 0);
        }
    }
}