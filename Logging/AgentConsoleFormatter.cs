using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace DigitalSignage.Agent.Logging
{
    /// <summary>
    /// Define un formato compacto para los registros de consola del agente.
    /// Muestra fecha, hora, nivel y mensaje sin incluir la categoría
    /// del servicio ni el identificador del evento.
    /// </summary>
    public sealed class AgentConsoleFormatter : ConsoleFormatter
    {
        /// <summary>
        /// Inicializa el formateador personalizado de consola.
        /// </summary>
        public AgentConsoleFormatter() : base("AgentConsole")
        {
        }

        /// <summary>
        /// Escribe un evento de logging utilizando el formato
        /// definido para DigitalSignage.Agent.
        /// </summary>
        /// <typeparam name="TState">
        /// Tipo del estado asociado al evento de logging.
        /// </typeparam>
        /// <param name="logEntry">
        /// Información del evento, incluyendo nivel, mensaje y excepción.
        /// </param>
        /// <param name="scopeProvider">
        /// Proveedor de ámbitos de logging.
        /// </param>
        /// <param name="textWriter">
        /// Destino donde se escribe el registro formateado.
        /// </param>
        public override void Write<TState>(
            in LogEntry<TState> logEntry,
            IExternalScopeProvider? scopeProvider,
            TextWriter textWriter)
        {
            string? message = logEntry.Formatter(
                logEntry.State,
                logEntry.Exception);

            if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
            {
                return;
            }

            string level = logEntry.LogLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRITICAL",
                _ => "UNKNOWN"
            };

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            textWriter.WriteLine(
                $"{timestamp} [{level}] {message}");

            // Conserva la excepción completa, incluida su traza,
            // para facilitar el diagnóstico de errores.
            if (logEntry.Exception is not null)
            {
                textWriter.WriteLine(logEntry.Exception);
            }
        }
    }
}