using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Models
{
    /// <summary>
    /// Representa la estructura general de las respuestas
    /// enviadas por la API de Digital Signage.
    /// </summary>
    /// <typeparam name="T">
    /// Tipo de información contenida en la propiedad Data.
    /// </typeparam>
    public sealed class ApiResponse<T>
    {
        public string Status { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public T? Data { get; set; }
    }
}
