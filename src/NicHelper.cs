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
                   adapter.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.Wwanpp ||
                   adapter.NetworkInterfaceType == NetworkInterfaceType.Wwanpp2;
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
            return GetCandidateAdapters(
                requireUp,
                preferGateway,
                AppSettings.FilterVirtualAdapters,
                AppSettings.FilterNoGatewayAdapters,
                AppSettings.FilterUncommonAdapterTypes,
                AppSettings.FilterNoUsableAddressAdapters);
        }

        /// <summary>
        /// 保留原有二参数入口，并允许调用方显式覆盖用户筛选设置。
        /// </summary>
        public static IEnumerable<NetworkInterface> GetCandidateAdapters(
            bool requireUp,
            bool preferGateway,
            bool filterVirtualAdapters,
            bool filterNoGatewayAdapters)
        {
            return GetCandidateAdapters(
                requireUp,
                preferGateway,
                filterVirtualAdapters,
                filterNoGatewayAdapters,
                true);
        }

        public static IEnumerable<NetworkInterface> GetCandidateAdapters(
            bool requireUp,
            bool preferGateway,
            bool filterVirtualAdapters,
            bool filterNoGatewayAdapters,
            bool filterUncommonAdapterTypes)
        {
            return GetCandidateAdapters(
                requireUp,
                preferGateway,
                filterVirtualAdapters,
                filterNoGatewayAdapters,
                filterUncommonAdapterTypes,
                false);
        }

        public static IEnumerable<NetworkInterface> GetCandidateAdapters(
            bool requireUp,
            bool preferGateway,
            bool filterVirtualAdapters,
            bool filterNoGatewayAdapters,
            bool filterUncommonAdapterTypes,
            bool filterNoUsableAddressAdapters)
        {
            var upAdapters = new List<NetworkInterface>();
            var usableAdapters = new List<NetworkInterface>();
            var gatewayAdapters = new List<NetworkInterface>();

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (filterUncommonAdapterTypes && !IsTargetNicType(adapter)) continue;
                if (requireUp && adapter.OperationalStatus != OperationalStatus.Up) continue;

                IPInterfaceProperties properties;
                bool hasProperties = TryGetIPProperties(adapter, out properties);
                bool hasUsableGateway = hasProperties && HasUsableGateway(properties);
                bool hasUsableAddress = hasProperties && HasUsableUnicastAddress(properties);

                // 无网关筛选优先于虚拟网卡关键词筛选。
                if (filterNoGatewayAdapters && !hasUsableGateway) continue;
                if (filterVirtualAdapters && IsCommonVirtualNic(adapter)) continue;
                if (filterNoUsableAddressAdapters && !hasUsableAddress) continue;

                upAdapters.Add(adapter);
                if (!hasProperties) continue;

                if (hasUsableGateway || hasUsableAddress)
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
                if (filterNoUsableAddressAdapters) return usableAdapters;

                var allAdapters = new List<NetworkInterface>(usableAdapters);
                allAdapters.AddRange(upAdapters.Where(n => !usableAdapters.Any(u => u.Id == n.Id)));
                return allAdapters;
            }

            var result = new List<NetworkInterface>();
            result.AddRange(gatewayAdapters);
            result.AddRange(usableAdapters.Where(n => !gatewayAdapters.Any(g => g.Id == n.Id)));
            if (!filterNoUsableAddressAdapters)
            {
                result.AddRange(upAdapters.Where(n => !result.Any(r => r.Id == n.Id)));
            }
            return result;
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
