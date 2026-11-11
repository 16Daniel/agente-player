
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalSignage.Agent.Models;
using Microsoft.Extensions.Logging;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// - Generación automática de claves, una sola vez por Raspberry.
    /// - Administra la identidad criptográfica del dispositivo mediante
    ///   un par de claves RSA-3072 almacenadas localmente.
    /// - Almacenamiento persistente en /var/lib/digital-signage/state/.
    /// 
    /// </summary>
    public sealed class DeviceIdentityService
    {
        private const int KeySize = 3072; // tamaño de la llave RSA en bits (mínimo recomendado para 2024).

        // / Directorio donde se almacenan las claves del dispositivo.
        private const string IdentityDirectory = "/var/lib/digital-signage/state";

        // Ruta completa del archivo de la clave privada, con el nombre "private-key.pem" (lo mantiene el player, NO se comparte).
        private readonly string _privateKeyPath = Path.Combine(IdentityDirectory, "private-key.pem");

        // Ruta completa del archivo de la clave pública, con el nombre "public-key.pem" (lo comparte el player con la API).
        private readonly string _publicKeyPath = Path.Combine(IdentityDirectory, "public-key.pem");

        // Archivo que almacena el estado local de aprovisionamiento.
        private readonly string _identityStatePath = Path.Combine(IdentityDirectory, "identity-state.json");

        // Servicio de registro de eventos para informar sobre la generación y validación de claves.
        private readonly ILogger<DeviceIdentityService> _logger;

        /// <summary>
        /// Inicializa el servicio de identidad criptográfica.
        /// </summary>
        /// <param name="logger">Servicio de registro de eventos.</param>
        public DeviceIdentityService(ILogger<DeviceIdentityService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Inicializa y valida la identidad criptográfica del dispositivo.
        /// Genera claves únicamente durante el aprovisionamiento inicial
        /// y conserva el historial local de vinculación.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// La identidad está incompleta, dañada o requiere revinculación.
        /// </exception>
        public void EnsureIdentity()
        {
            try
            {
                DeviceIdentityState? state = LoadIdentityState();

                bool privateExists = File.Exists(_privateKeyPath);
                bool publicExists = File.Exists(_publicKeyPath);

                // Caso 1: No existen claves ni estado local.
                // Posible primera instalación.
                if (!privateExists && !publicExists && state == null)
                {
                    GenerateIdentity();

                    ValidateIdentity();

                    SaveIdentityState(new DeviceIdentityState
                    {
                        IdentityGenerated = true,
                        WasLinked = false,
                        CreatedAtUtc = DateTime.UtcNow
                    });

                    _logger.LogInformation(
                        "Identidad criptográfica inicial generada. " +
                        "Pendiente de vinculación con la API.");

                    return;
                }

                // Caso 2: La clave privada no existe.
                // No regenerar automáticamente una identidad anterior.
                if (!privateExists)
                {
                    throw new InvalidOperationException(
                        "La clave privada del dispositivo no existe. " +
                        "Se requiere recuperación o revinculación autorizada.");
                }

                // Caso 3: Existe una clave privada.
                // Validar su integridad y tamaño.
                using (RSA rsa = RSA.Create())
                {
                    rsa.ImportFromPem(File.ReadAllText(_privateKeyPath));

                    if (rsa.KeySize != KeySize)
                    {
                        throw new InvalidOperationException(
                            "La clave privada no utiliza RSA-3072.");
                    }
                }

                // Caso 4: Recuperar la clave pública si falta
                // o si no corresponde a la privada.
                if (!publicExists)
                {
                    RecoverPublicKey();
                }
                else
                {
                    try
                    {
                        ValidateIdentity();
                    }
                    catch (CryptographicException)
                    {
                        _logger.LogWarning(
                            "Clave pública inválida. Se intentará reconstruir.");

                        RecoverPublicKey();
                    }
                    catch (InvalidOperationException)
                    {
                        _logger.LogWarning(
                            "Las claves no corresponden. Se reconstruirá la pública.");

                        RecoverPublicKey();
                    }
                }

                ValidateIdentity();

                // Caso 5: Existen claves válidas, pero falta el JSON.
                // Recuperar el estado local sin declarar vinculación.
                if (state == null)
                {
                    state = new DeviceIdentityState
                    {
                        IdentityGenerated = true,
                        WasLinked = false,
                        CreatedAtUtc = null
                    };

                    SaveIdentityState(state);

                    _logger.LogWarning(
                        "Estado local ausente. Se reconstruyó sin asumir " +
                        "que el dispositivo esté vinculado.");
                }
                else if (!state.IdentityGenerated)
                {
                    state.IdentityGenerated = true;
                    SaveIdentityState(state);
                }

                _logger.LogInformation(
                    "Identidad criptográfica validada. " +
                    "Vinculación histórica: {WasLinked}.",
                    state.WasLinked);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "No fue posible inicializar la identidad criptográfica.");

                throw;
            }
        }

        /// <summary>
        /// GENERACION DE CLAVES RSA-3072
        /// Genera una nueva identidad RSA-3072 y la almacena de forma persistente.
        /// Utiliza archivos temporales y publicación atómica para evitar
        /// exponer archivos parcialmente escritos.
        /// </summary>
        /// <exception cref="IOException">
        /// Se produce cuando no es posible crear o publicar los archivos.
        /// </exception>
        private void GenerateIdentity()
        {
            Directory.CreateDirectory(IdentityDirectory);

            // Nunca reemplazar una identidad existente.
            if (File.Exists(_privateKeyPath) || File.Exists(_publicKeyPath))
            {
                throw new InvalidOperationException(
                    "Ya existen archivos de identidad. No se generarán nuevas claves.");
            }

            using RSA rsa = RSA.Create(KeySize);

            string privatePem = rsa.ExportPkcs8PrivateKeyPem();
            string publicPem = rsa.ExportSubjectPublicKeyInfoPem();

            string temporaryPrivatePath =
                _privateKeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            string temporaryPublicPath =
                _publicKeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                var privateOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode =
                        UnixFileMode.UserRead | UnixFileMode.UserWrite
                };

                var publicOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode =
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead |
                        UnixFileMode.OtherRead
                };

                // Escribir primero la clave privada.
                using (var stream = new FileStream(
                    temporaryPrivatePath, privateOptions))
                using (var writer = new StreamWriter(stream, Encoding.ASCII))
                {
                    writer.Write(privatePem);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // Escribir la clave pública.
                using (var stream = new FileStream(
                    temporaryPublicPath, publicOptions))
                using (var writer = new StreamWriter(stream, Encoding.ASCII))
                {
                    writer.Write(publicPem);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // Publicar primero la clave privada.
                // Si se interrumpe después, la pública podrá reconstruirse.
                File.Move(temporaryPrivatePath, _privateKeyPath);
                File.Move(temporaryPublicPath, _publicKeyPath);

                _logger.LogInformation(
                    "Nueva identidad criptográfica RSA-{KeySize} generada correctamente.",
                    KeySize);
            }
            finally
            {
                // Eliminar únicamente archivos temporales de esta operación.
                if (File.Exists(temporaryPrivatePath))
                {
                    File.Delete(temporaryPrivatePath);
                }

                if (File.Exists(temporaryPublicPath))
                {
                    File.Delete(temporaryPublicPath);
                }
            }
        }


        /// <summary>
        /// VALIDACION CRIPTOGRAFICA
        /// Comprueba que las claves almacenadas sean válidas
        /// y pertenezcan al mismo par criptográfico.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Las claves son inválidas o no corresponden entre sí.
        /// </exception>
        private void ValidateIdentity()
        {
            string privatePem = File.ReadAllText(_privateKeyPath);
            string publicPem = File.ReadAllText(_publicKeyPath);

            using RSA privateRsa = RSA.Create();
            using RSA publicRsa = RSA.Create();

            privateRsa.ImportFromPem(privatePem);
            publicRsa.ImportFromPem(publicPem);

            if (privateRsa.KeySize != KeySize ||
                publicRsa.KeySize != KeySize)
            {
                throw new InvalidOperationException(
                    "La identidad criptográfica no utiliza RSA-3072.");
            }

            byte[] challenge = RandomNumberGenerator.GetBytes(32);

            byte[] signature = privateRsa.SignData(
                challenge,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);

            bool valid = publicRsa.VerifyData(
                challenge,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);

            if (!valid)
            {
                throw new InvalidOperationException(
                    "La clave pública no corresponde a la clave privada.");
            }
        }


        /// <summary>
        /// Obtiene el estado local de vinculación del dispositivo.
        /// No verifica si la autorización sigue vigente en el servidor.
        /// </summary>
        /// <returns>Estado de identidad almacenado localmente.</returns>
        /// <exception cref="InvalidOperationException">
        /// El estado local no existe o no es válido.
        /// </exception>
        public DeviceIdentityState GetIdentityState()
        {
            DeviceIdentityState? state = LoadIdentityState();

            if (state == null)
            {
                throw new InvalidOperationException(
                    "No existe el estado local de identidad del dispositivo.");
            }

            return state;
        }

        /// <summary>
        /// Obtiene la clave pública del dispositivo en formato PEM.
        /// </summary>
        /// <returns>Clave pública RSA en formato PEM.</returns>
        public string GetPublicKey()
        {
            return File.ReadAllText(_publicKeyPath);
        }

        /// <summary>
        /// Firma un desafío utilizando la clave privada del dispositivo.
        /// </summary>
        /// <param name="challenge">
        /// Datos del desafío que se deben firmar.
        /// </param>
        /// <returns>Firma digital RSA-PSS con SHA-256.</returns>
        public byte[] SignChallenge(byte[] challenge)
        {
            ArgumentNullException.ThrowIfNull(challenge);

            string privatePem = File.ReadAllText(_privateKeyPath);

            using RSA rsa = RSA.Create();

            rsa.ImportFromPem(privatePem);

            return rsa.SignData(
                challenge,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }


        /// <summary>
        /// RECUPERACION DE CLAVE PUBLICA
        /// Reconstruye la clave pública RSA-3072 a partir de la clave privada.
        /// No modifica ni reemplaza la clave privada del dispositivo.
        /// </summary>
        /// <exception cref="CryptographicException">
        /// La clave privada no contiene una identidad RSA válida.
        /// </exception>
        /// <exception cref="IOException">
        /// No es posible guardar o publicar la clave pública.
        /// </exception>
        private void RecoverPublicKey()
        {
            string privatePem = File.ReadAllText(_privateKeyPath);

            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(privatePem);

            if (rsa.KeySize != KeySize)
            {
                throw new InvalidOperationException(
                    "No se puede recuperar la clave pública: " +
                    "la clave privada no utiliza RSA-3072.");
            }

            string publicPem = rsa.ExportSubjectPublicKeyInfoPem();

            string temporaryPath =
                _publicKeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode =
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead |
                        UnixFileMode.OtherRead
                };

                using (var stream = new FileStream(temporaryPath, options))
                using (var writer = new StreamWriter(stream, Encoding.ASCII))
                {
                    writer.Write(publicPem);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // Reemplazar únicamente la clave pública.
                File.Move(temporaryPath, _publicKeyPath, overwrite: true);

                _logger.LogInformation(
                    "Clave pública RSA-3072 recuperada correctamente.");
            }
            finally
            {
                // Eliminar únicamente el temporal de esta operación.
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }


        /// <summary>
        /// LECTURA DEL JSON
        /// Lee el estado local de aprovisionamiento del dispositivo.
        /// </summary>
        /// <returns>
        /// Estado almacenado o null cuando el archivo no existe.
        /// </returns>
        /// <exception cref="JsonException">
        /// El archivo contiene información JSON inválida.
        /// </exception>
        private DeviceIdentityState? LoadIdentityState()
        {
            if (!File.Exists(_identityStatePath))
            {
                return null;
            }

            string json = File.ReadAllText(_identityStatePath);

            DeviceIdentityState? state =
                JsonSerializer.Deserialize<DeviceIdentityState>(json);

            if (state == null)
            {
                throw new InvalidOperationException(
                    "El archivo de estado de identidad no contiene información válida.");
            }

            return state;
        }


        /// <summary>
        /// ESCRITURA SEGURA DEL JSON
        /// Guarda de forma persistente el estado local de aprovisionamiento.
        /// Utiliza un archivo temporal para evitar escrituras parciales.
        /// </summary>
        /// <param name="state">Estado de identidad que se almacenará.</param>
        /// <exception cref="IOException">
        /// No fue posible guardar el archivo de estado.
        /// </exception>
        private void SaveIdentityState(DeviceIdentityState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            Directory.CreateDirectory(IdentityDirectory);

            string temporaryPath =
                _identityStatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                string json = JsonSerializer.Serialize(
                    state,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode =
                        UnixFileMode.UserRead | UnixFileMode.UserWrite
                };

                using (var stream = new FileStream(temporaryPath, options))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _identityStatePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

    }
}
