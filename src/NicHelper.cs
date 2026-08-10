using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetInfoCheckerX
{
    internal sealed class NicAddressInfo
    {
        public NetworkInterface Adapter { get; set; }
        public IPAddress Address { get; set; }
        public string AddressText { get; set; }
        public string DisplayText { get; set; }
    }

    internal static class NicHelper
    {
        public static bool IsTargetNicType(NetworkInterface adapter)
        {
            return adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.Wwanpp;
        }

        public static bool IsCommonVirtualNic(NetworkInterface adapter)
        {
            string name = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
            return name.Contains("virtual") || name.Contains("vmware") ||
                   name.Contains("hyper-v") || name.Contains("wsl") ||
                   name.Contains("pseudo") || name.Contains("tap") ||
                   name.Contains("tun") || name.Contains("loopback") ||
                   name.Contains("vpn") || name.Contains("vbox") || name.Contains("teredo");
        }

        public static bool TryGetIPProperties(NetworkInterface adapter, out IPInterfaceProperties properties)
        {
            try
            {
                properties = adapter.GetIPProperties();
                return true;
            }
            catch (NetworkInformationException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            properties = null;
            return false;
        }

        public static bool TryGetIPv4Properties(IPInterfaceProperties properties, out IPv4InterfaceProperties ipv4Properties)
        {
            try
            {
                ipv4Properties = properties.GetIPv4Properties();
                return ipv4Properties != null;
            }
            catch (NetworkInformationException)
            {
            }
            catch (SocketException)
            {
            }

            ipv4Properties = null;
            return false;
        }

        public static bool IsUsableGatewayAddress(IPAddress address)
        {
            if (address == null) return false;
            if (IPAddress.IsLoopback(address)) return false;
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6Any)) return false;
            if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6) return false;

            return true;
        }

        public static bool HasUsableGateway(IPInterfaceProperties properties)
        {
            return properties.GatewayAddresses.Any(g => IsUsableGatewayAddress(g.Address));
        }

        public static bool IsUsableUnicastAddress(UnicastIPAddressInformation ipInfo)
        {
            IPAddress address = ipInfo.Address;
            if (address == null || IPAddress.IsLoopback(address)) return false;
            if (ipInfo.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Duplicate) return false;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                return !address.Equals(IPAddress.Any) &&
                       !address.Equals(IPAddress.None) &&
                       !(bytes[0] == 169 && bytes[1] == 254);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return !address.IsIPv6Multicast &&
                       !address.IsIPv6LinkLocal &&
                       !address.IsIPv6SiteLocal &&
                       !address.Equals(IPAddress.IPv6Any) &&
                       !address.Equals(IPAddress.IPv6Loopback);
            }

            return false;
        }

        public static bool HasUsableUnicastAddress(IPInterfaceProperties properties)
        {
            return properties.UnicastAddresses.Any(IsUsableUnicastAddress);
        }

        public static IEnumerable<NetworkInterface> GetCandidateAdapters(bool requireUp, bool preferGateway)
        {
            var upAdapters = new List<NetworkInterface>();
            var usableAdapters = new List<NetworkInterface>();
            var gatewayAdapters = new List<NetworkInterface>();

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsTargetNicType(adapter) || IsCommonVirtualNic(adapter)) continue;
                if (requireUp && adapter.OperationalStatus != OperationalStatus.Up) continue;

                upAdapters.Add(adapter);

                if (!TryGetIPProperties(adapter, out IPInterfaceProperties properties)) continue;

                bool hasUsableGateway = HasUsableGateway(properties);
                if (hasUsableGateway || HasUsableUnicastAddress(properties))
                {
                    usableAdapters.Add(adapter);
                    if (hasUsableGateway)
                    {
                        gatewayAdapters.Add(adapter);
                    }
                }
            }

            if (!preferGateway)
            {
                return usableAdapters.Count > 0 ? usableAdapters : upAdapters;
            }

            var result = new List<NetworkInterface>();
            result.AddRange(gatewayAdapters);
            result.AddRange(usableAdapters.Where(n => !gatewayAdapters.Any(g => g.Id == n.Id)));
            return result.Count > 0 ? result : upAdapters;
        }

        public static IEnumerable<NicAddressInfo> GetUsableIPAddresses(bool includeIPv4 = true, bool includeIPv6 = true)
        {
            foreach (NetworkInterface adapter in GetCandidateAdapters(requireUp: true, preferGateway: true))
            {
                if (!TryGetIPProperties(adapter, out IPInterfaceProperties properties)) continue;

                foreach (UnicastIPAddressInformation ipInfo in properties.UnicastAddresses)
                {
                    if (!IsUsableUnicastAddress(ipInfo)) continue;

                    IPAddress ip = ipInfo.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !includeIPv4) continue;
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !includeIPv6) continue;

                    string ipText = ip.ToString();
                    int scopeIndex = ipText.IndexOf('%');
                    if (scopeIndex >= 0) ipText = ipText.Substring(0, scopeIndex);

                    yield return new NicAddressInfo
                    {
                        Adapter = adapter,
                        Address = ip,
                        AddressText = ipText,
                        DisplayText = $"{ipText} ({adapter.Name})"
                    };
                }
            }
        }

        public static IPAddress GetFirstSystemDns(AddressFamily addressFamily)
        {
            foreach (NetworkInterface adapter in GetCandidateAdapters(requireUp: true, preferGateway: true))
            {
                if (!TryGetIPProperties(adapter, out IPInterfaceProperties properties)) continue;

                IPAddress dns = properties.DnsAddresses.FirstOrDefault(addr => addr.AddressFamily == addressFamily);
                if (dns != null) return dns;
            }

            return null;
        }
    }
}
