using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetInfoCheckerX
{
    internal static class PrivacyHelper
    {
        private static readonly HashSet<string> PublicDnsWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "114.114.114.114",
            "114.114.115.115",
            "119.29.29.29",
            "119.28.28.28",
            "182.254.116.116",
            "2402:4e00::",
            "101.226.4.6",
            "218.30.118.6",
            "123.125.81.6",
            "140.207.198.6",
            "1.2.4.8",
            "210.2.4.8",
            "8.8.8.8",
            "8.8.4.4",
            "2001:4860:4860::8888",
            "2001:4860:4860::8844",
            "1.1.1.1",
            "1.0.0.1",
            "2606:4700:4700::1111",
            "2606:4700:4700::1001",
            "9.9.9.9",
            "149.112.112.112",
            "2620:fe::fe",
            "2620:fe::9",
            "185.222.222.222",
            "185.184.222.222",
            "2a09::",
            "2a11::",
            "208.67.222.222",
            "208.67.220.220",
            "2620:0:ccc::2",
            "2620:0:ccd::2",
            "199.91.73.222",
            "178.79.131.110",
            "223.5.5.5",
            "223.6.6.6",
            "2400:3200::1",
            "2400:3200:baba::1",
            "183.60.83.19",
            "183.60.82.98",
            "180.76.76.76",
            "2400:da00::6666",
            "4.2.2.1",
            "4.2.2.2",
            "122.112.208.1",
            "139.9.23.90",
            "114.115.192.11",
            "116.205.5.1",
            "116.205.5.30",
            "122.112.208.175",
            "139.159.208.206",
            "180.184.1.1",
            "180.184.2.2",
            "117.50.11.11",
            "52.80.66.66",
            "117.50.10.10",
            "52.80.52.52",
            "117.50.60.30",
            "52.80.60.30",
            "168.95.192.1",
            "168.95.1.1",
            "203.80.96.10",
            "203.80.96.9",
            "199.85.126.10",
            "199.85.127.10",
            "216.146.35.35",
            "216.146.36.36",
            "64.6.64.6",
            "64.6.65.6"
        };

        public static string MaskIP(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            string ipOnly = text;
            int scopeIndex = ipOnly.IndexOf('%');
            if (scopeIndex >= 0) ipOnly = ipOnly.Substring(0, scopeIndex);

            if (!IPAddress.TryParse(ipOnly, out IPAddress ipAddress)) return text;

            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                string[] parts = ipOnly.Split('.');
                if (parts.Length == 4) return $"{parts[0]}.*.*.{parts[3]}";
            }

            if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                string[] parts = ipOnly.Split(':');
                int firstIndex = Array.FindIndex(parts, p => !string.IsNullOrEmpty(p));
                int lastIndex = Array.FindLastIndex(parts, p => !string.IsNullOrEmpty(p));

                if (firstIndex < 0 || lastIndex < 0 || firstIndex == lastIndex) return ipOnly;

                for (int i = firstIndex + 1; i < lastIndex; i++)
                {
                    if (!string.IsNullOrEmpty(parts[i]))
                    {
                        parts[i] = "*";
                    }
                }

                return string.Join(":", parts);
            }

            return text;
        }

        public static string MaskIPIfPublic(string text)
        {
            return IsPublicIP(text) ? MaskIP(text) : text;
        }

        public static string MaskDnsIfNeeded(string text)
        {
            if (!IsPublicIP(text) || IsKnownPublicDns(text)) return text;
            return MaskIP(text);
        }

        public static string MaskIPsInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            string result = Regex.Replace(text,
                @"\b(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\.(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\.(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\.(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\b",
                match => MaskIP(match.Value));

            result = Regex.Replace(result,
                @"(?i)(?<![\w:])(?:[a-f0-9]{1,4}:){2,}[a-f0-9]{1,4}(?![\w:])|(?i)(?<![\w:])(?:[a-f0-9]{1,4}:){1,7}:(?![\w:])|(?i)(?<![\w:])::(?:[a-f0-9]{1,4}:){0,6}[a-f0-9]{1,4}(?![\w:])",
                match => MaskIP(match.Value));

            return result;
        }

        public static string MaskIPAndHideTailAfterFirstIP(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            Match match = Regex.Match(text,
                @"\b(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\.(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\.(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\.(?:25[0-5]|2[0-4][0-9]|[01]?\d?\d)\b|(?i)(?<![\w:])(?:[a-f0-9]{1,4}:){2,}[a-f0-9]{1,4}(?![\w:])|(?i)(?<![\w:])(?:[a-f0-9]{1,4}:){1,7}:(?![\w:])|(?i)(?<![\w:])::(?:[a-f0-9]{1,4}:){0,6}[a-f0-9]{1,4}(?![\w:])");

            if (!match.Success) return text;

            string maskedIp = MaskIP(match.Value);
            string prefix = text.Substring(0, match.Index);
            string suffix = text.Substring(match.Index + match.Length);
            return string.IsNullOrWhiteSpace(suffix)
                ? prefix + maskedIp
                : prefix + maskedIp + " (隐私模式)";
        }

        public static string MaskSpeedTestCnIP(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return ip;

            string[] parts = ip.Split('.');
            if (parts.Length == 4 && parts[3] == "*")
            {
                return $"{parts[0]}.*.*.* (隐私模式)";
            }

            return MaskIP(ip);
        }

        public static bool IsPublicIP(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string ipOnly = text;
            int scopeIndex = ipOnly.IndexOf('%');
            if (scopeIndex >= 0) ipOnly = ipOnly.Substring(0, scopeIndex);

            if (!IPAddress.TryParse(ipOnly, out _)) return false;
            return string.IsNullOrEmpty(IanaReservedIP.Check(ipOnly));
        }

        private static bool IsKnownPublicDns(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string ipOnly = text;
            int scopeIndex = ipOnly.IndexOf('%');
            if (scopeIndex >= 0) ipOnly = ipOnly.Substring(0, scopeIndex);

            if (!IPAddress.TryParse(ipOnly, out IPAddress ipAddress)) return false;
            return PublicDnsWhitelist.Contains(ipAddress.ToString());
        }
    }
}
