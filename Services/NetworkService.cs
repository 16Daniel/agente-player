using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DigitalSignage.Agent.Services
{
    public class NetworkService
    {
        /// <summary>
        /// Obtiene las direcciones IPv4 de las interfaces de red
        /// que se encuentran operativas en el player.
        /// </summary>
        /// <returns>
        /// Lista de direcciones IPv4 disponibles.
        /// </returns>
        public List<IPAddress> GetIPv4Addresses()
        {
            var addresses = new List<IPAddress>();

            foreach (NetworkInterface networkInterface
                in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (networkInterface.NetworkInterfaceType ==
                    NetworkInterfaceType.Loopback)
                    continue;

                IPInterfaceProperties properties =
                    networkInterface.GetIPProperties();

                foreach (UnicastIPAddressInformation address
                    in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily ==
                        AddressFamily.InterNetwork)
                    {
                        addresses.Add(address.Address);
                    }
                }
            }

            return addresses;
        }

        /// <summary>
        /// Obtiene la dirección IPv4 local que el sistema operativo seleccionaría para comunicarse con un destino 
        /// específico, dando prioridad a la interfaz de red más adecuada basandose en la metrica.
        /// </summary>
        /// <param name="destination">
        /// Dirección IPv4 del servidor de destino.
        /// </param>
        /// <returns>
        /// Dirección IPv4 local seleccionada por el sistema operativo.
        /// </returns>
        public IPAddress GetPreferredIPv4Address(IPAddress destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (destination.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException(
                    "El destino debe ser una dirección IPv4.",
                    nameof(destination));
            }

            using var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);

            socket.Connect(new IPEndPoint(destination, 443));

            if (socket.LocalEndPoint is not IPEndPoint localEndPoint)
            {
                throw new InvalidOperationException(
                    "No fue posible determinar la dirección IPv4 local.");
            }

            return localEndPoint.Address;
        }

        /// <summary>
        /// Obtiene la dirección MAC de la interfaz de red que tiene asignada la direccion IPv4 indicada,
        /// es decir la que tiene conectada en el player, si cambia automaticamente la direccion IP
        /// tambien cambia la direccion MAC
        /// </summary>
        /// <param name="ipAddress">
        /// Direccion de IPv4 de la interfaz de red
        /// </param>
        /// <returns>
        /// Direccion MAC de la interfaz correspondiente
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// La dirección IP proporcionada es nula
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// No se encontró una interfaz de red asociada a la dirección IP.
        /// </exception>
        public string GetMacAddressByIPv4(IPAddress ipAddress) {
            ArgumentNullException.ThrowIfNull(ipAddress);
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces()) {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                IPInterfaceProperties properties = networkInterface.GetIPProperties();

                bool hasAddress = properties.UnicastAddresses.Any( address => address.Address.Equals(ipAddress));

                if (hasAddress) {
                    string macAddress = networkInterface.GetPhysicalAddress().ToString();

                    return string.Join(":",Enumerable.Range(0, macAddress.Length / 2)
                                                .Select(i => macAddress.Substring(i * 2, 2))).ToUpperInvariant();
                }

            }
            throw new InvalidOperationException($"No se encontró una interfaz de red para la IP {ipAddress}.");
        }
    }

}

