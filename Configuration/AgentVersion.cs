using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace DigitalSignage.Agent.Configuration
{
    public static class AgentVersion
    {

        /// <summary>
        /// Obtiene la versión del agente en formato Major.Minor.Patch. ejemplo: (1.0.0)
        /// </summary>
        public static string Current { get; } =
          typeof(AgentVersion).Assembly
              .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
              .InformationalVersion.Split('+')[0]
              ?? "0.0.0";

    }
}
