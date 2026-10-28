using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalSignage.Agent.Services
{
    /// <summary>
    /// Obtiene la información de identificación del hardware
    /// de una Raspberry Pi mediante los archivos del sistema Linux.
    /// </summary>
    public class HardwareService
    {
        private const string ModelPath = "/proc/device-tree/model";
        private const string SerialPath = "/proc/device-tree/serial-number";
        private const string MacPath = "/sys/class/net/eth0/address";
        private const string MacWlanPath = "/sys/class/net/wlan0/address";
        private const string StorageSizePath = "/sys/block/mmcblk0/size";


        /// <summary>
        /// Obtiene el modelo del dispositivo.
        /// </summary>
        public string GetModel()
        {
            return ReadSystemFile(ModelPath);
        }

        /// <summary>
        /// Obtiene el número de serie del hardware.
        /// </summary>
        public string GetSerialNumber()
        {
            return ReadSystemFile(SerialPath);
        }

        /// <summary>
        /// Obtiene la dirección MAC de Ethernet.
        /// </summary>
        public string GetMacAddress()
        {
            return ReadSystemFile(MacPath).ToUpperInvariant();
        }
        /// <summary>
        /// Obtiene la dirección MAC de Wifi.
        /// </summary>
        public string GetMacWlanAddress()
        {
            return ReadSystemFile(MacWlanPath).ToUpperInvariant();
        }

        /// <summary>
        /// Lee un archivo del sistema y elimina espacios y
        /// caracteres nulos presentes en los datos.
        /// </summary>
        private static string ReadSystemFile(string path)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException(
                    "La identificación de hardware requiere Linux.");
            }

            try
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"No se encontró el archivo de identificación: {path}", path);
                }

                return File.ReadAllText(path).Trim('\0', '\r', '\n', ' ');
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"No fue posible leer la información de hardware desde '{path}'.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"No se tienen permisos para leer '{path}'.", ex);
            }
        }

        /// <summary>
        /// Obtiene la capacidad física de la tarjeta microSD
        /// de la Raspberry Pi, expresada en bytes.
        /// </summary>
        /// <returns>Capacidad física de la microSD en bytes.</returns>
        /// <exception cref="PlatformNotSupportedException">
        /// Se produce cuando el sistema operativo no es Linux.
        /// </exception>
        /// <exception cref="FormatException">
        /// Se produce cuando el sistema devuelve un tamaño inválido.
        /// </exception>
        /// <exception cref="OverflowException">
        /// Se produce cuando el tamaño excede el rango permitido.
        /// </exception>
        public long GetStorageCapacityBytes()
        {
            string sectors = ReadSystemFile(StorageSizePath);

            long sectorCount = long.Parse(
                sectors,
                System.Globalization.CultureInfo.InvariantCulture);

            // Linux informa el tamaño mediante sectores de 512 bytes.
            return checked(sectorCount * 512L);
        }

        /// <summary>
        /// Obtiene el espacio disponible en el sistema de archivos
        /// donde se almacena el contenido multimedia del Agent.
        /// </summary>
        /// <returns>Espacio disponible expresado en bytes.</returns>
        /// <exception cref="DirectoryNotFoundException">
        /// Se produce cuando no existe la ruta de almacenamiento.
        /// </exception>
        /// <exception cref="IOException">
        /// Se produce cuando no puede consultarse el almacenamiento.
        /// </exception>
        public long GetAvailableStorageBytes()
        {
            const string mediaPath = "/var/lib/digital-signage/media";  

            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException(
                    "La consulta de almacenamiento requiere Linux.");
            }

            if (!Directory.Exists(mediaPath))
            {
                throw new DirectoryNotFoundException(
                    $"No existe el directorio de contenido multimedia: {mediaPath}");
            }

            DriveInfo drive = new DriveInfo(mediaPath);

            return drive.AvailableFreeSpace;
        }





    }
}
