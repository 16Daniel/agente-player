namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa la configuración y sus versiones
    /// registradas para un Player en la API central.
    /// </summary>
    public sealed class PlayerConfiguracion
    {
        public int id_player { get; set; }

        public int? id_audio { get; set; }

        public string? codigo { get; set; }

        public int? version_configuracion { get; set; }

        public int? version_aplicada { get; set; }
    }
}