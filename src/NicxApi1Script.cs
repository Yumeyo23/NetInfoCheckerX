using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    internal sealed class MultiEduApiProvider
    {
        internal string Group { get; set; }
        internal int ID { get; set; }
        internal string Name { get; set; }
        internal string ToolTip { get; set; }
        internal Func<CancellationToken, Task<string>> GetIP { get; set; }
    }

    internal sealed class MultiFullApiProvider
    {
        internal string Group { get; set; }
        internal int ID { get; set; }
        internal string Name { get; set; }
        internal string ToolTip { get; set; }
        internal Func<CancellationToken, Action<string>, Task<string>> GetIP { get; set; }
    }

    internal sealed class MultiLiteApiProvider
    {
        internal string Group { get; set; }
        internal int ID { get; set; }
        internal string Name { get; set; }
        internal string ToolTip { get; set; }
        internal Func<IPAddress, CancellationToken, Action<string>, Task<string>> GetIP { get; set; }
    }

    /// <summary>
    /// NICX 外置 API 配置文件解释器
    /// </summary>
    internal static class NicxApi1Script
    {
        internal const string FileName = "NICX_Api1.nicxapi";
        internal const string MultiEduFileName = "NICX_MultiEdu.nicxapi";
        internal const string MultiFullFileName = "NICX_MultiFull.nicxapi";
        internal const string MultiLiteFileName = "NICX_MultiLite.nicxapi";

        private static readonly object GeoCacheLock = new object();
        private static readonly Random ScriptRandom = new Random();
        private static readonly Dictionary<string, CachedGeoValue> GeoCache =
            new Dictionary<string, CachedGeoValue>(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedGeoValue
        {
            internal string Value;
            internal DateTime ExpiresUtc;
        }

        internal static bool TryLoad(
            IList<ApiProvider> ipcn,
            IList<ApiProvider> ipcnyx,
            IList<ApiProvider> ipgfw,
            IDictionary<string, string> toolTips)
        {
            string assemblyPath = typeof(Api1).Assembly.Location;
            string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyPath);
            string path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path)) return false;

            try
            {
                string source = File.ReadAllText(path, new UTF8Encoding(false, true));
                var catalog = new Parser(source).Parse();
                ValidateCatalog(catalog);

                var stagedCN = new List<ApiProvider>();
                var stagedYX = new List<ApiProvider>();
                var stagedGFW = new List<ApiProvider>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ProviderDefinition definition in catalog.Providers)
                {
                    IList<ApiProvider> target;
                    switch (definition.Group.ToUpperInvariant())
                    {
                        case "IPCN": target = stagedCN; break;
                        case "IPCNYX": target = stagedYX; break;
                        case "IPGFW": target = stagedGFW; break;
                        default: throw new FormatException("未知 Provider 分组: " + definition.Group);
                    }

                    string identity = definition.Group + ":" + definition.ID.ToString(CultureInfo.InvariantCulture);
                    if (!seen.Add(identity))
                        throw new FormatException("Provider ID 重复: " + identity);

                    EndpointDefinition ipv4 = definition.IPv4;
                    EndpointDefinition ipv6 = definition.IPv6;
                    if (ipv4 == null && ipv6 == null)
                        throw new FormatException(identity + " 没有 ipv4/ipv6 实现");

                    target.Add(new ApiProvider
                    {
                        ID = definition.ID,
                        Name = definition.ID.ToString(CultureInfo.InvariantCulture),
                        GetIP4 = ipv4 == null ? null : new Func<CancellationToken, Task<string>>(
                            token => ExecuteAsync(ipv4, token)),
                        GetIP6 = ipv6 == null ? null : new Func<CancellationToken, Task<string>>(
                            token => ExecuteAsync(ipv6, token))
                    });
                }

                // 当前 API1 以 ID 同时作为显示序号；按 ID 排序可保持原有列表顺序，
                // 修改外置文件中的 ID 后，显示顺序也随之变化。
                stagedCN.Sort((left, right) => left.ID.CompareTo(right.ID));
                stagedYX.Sort((left, right) => left.ID.CompareTo(right.ID));
                stagedGFW.Sort((left, right) => left.ID.CompareTo(right.ID));

                AppendProviders(ipcn, stagedCN);
                AppendProviders(ipcnyx, stagedYX);
                AppendProviders(ipgfw, stagedGFW);

                foreach (KeyValuePair<string, string> pair in catalog.ToolTips)
                    toolTips[pair.Key] = pair.Value;

                return true;
            }
            catch (Exception ex)
            {
                TryWriteError(path, ex);
                return false;
            }
        }

        internal static bool TryLoadGeo(
            IList<GeoProvider> geoCN,
            IList<GeoProvider> geoGFW,
            IDictionary<string, string> toolTips)
        {
            string assemblyPath = typeof(Api2).Assembly.Location;
            string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyPath);
            string path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                "NICX_Api2.nicxapi");
            if (!File.Exists(path)) return false;

            try
            {
                string source = File.ReadAllText(path, new UTF8Encoding(false, true));
                CatalogDefinition catalog = new Parser(source).Parse();
                ValidateCatalog(catalog);

                var stagedCN = new List<GeoProvider>();
                var stagedGFW = new List<GeoProvider>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ProviderDefinition definition in catalog.Providers)
                {
                    if (string.IsNullOrWhiteSpace(definition.GeoReference)) continue;

                    EndpointDefinition endpoint;
                    if (!catalog.GeoEndpoints.TryGetValue(definition.GeoReference, out endpoint))
                        throw new FormatException("找不到 geo 方法: " + definition.GeoReference);

                    IList<GeoProvider> target;
                    switch (definition.Group.ToUpperInvariant())
                    {
                        case "GEOCN": target = stagedCN; break;
                        case "GEOGFW": target = stagedGFW; break;
                        default: throw new FormatException("未知 API2 Provider 分组: " + definition.Group);
                    }

                    string identity = definition.Group + ":" + definition.ID.ToString(CultureInfo.InvariantCulture);
                    if (!seen.Add(identity)) throw new FormatException("Provider ID 重复: " + identity);
                    target.Add(new GeoProvider
                    {
                        ID = definition.ID,
                        Name = definition.ID.ToString(CultureInfo.InvariantCulture),
                        IsLocalDatabase = false,
                        GetGeoTask = (ip, token) => ExecuteGeoAsync(endpoint, ip, token)
                    });
                }

                stagedCN.Sort((left, right) => left.ID.CompareTo(right.ID));
                stagedGFW.Sort((left, right) => left.ID.CompareTo(right.ID));
                AppendGeoProviders(geoCN, stagedCN);
                AppendGeoProviders(geoGFW, stagedGFW);
                foreach (KeyValuePair<string, string> pair in catalog.ToolTips)
                    toolTips[pair.Key] = pair.Value;
                return true;
            }
            catch (Exception ex)
            {
                TryWriteGeoError(path, ex);
                return false;
            }
        }

        internal static bool TryLoadMultiEdu(
            IList<MultiEduApiProvider> providers,
            out string errorMessage)
        {
            string assemblyPath = typeof(MultiEdu).Assembly.Location;
            string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyPath);
            string path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                MultiEduFileName);

            if (!File.Exists(path))
            {
                errorMessage = "未找到教育网 API 配置文件：\r\n" + path;
                return false;
            }

            try
            {
                string source = File.ReadAllText(path, new UTF8Encoding(false, true));
                CatalogDefinition catalog = new Parser(source).Parse();
                ValidateCatalog(catalog);

                var staged = new List<MultiEduApiProvider>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ProviderDefinition definition in catalog.Providers)
                {
                    string group = definition.Group.ToUpperInvariant();
                    if (group != "MULTIEDUPAIR" && group != "MULTIEDUV4" &&
                        group != "MULTIEDUDUAL")
                        throw new FormatException("未知 MultiEdu Provider 分组: " + definition.Group);
                    if (!string.IsNullOrWhiteSpace(definition.GeoReference))
                        throw new FormatException("MULTIEDU Provider 不支持 use");
                    if (definition.ID < 1)
                        throw new FormatException(group + " Provider ID 必须大于 0: " + definition.ID);
                    string identity = group + ":" +
                        definition.ID.ToString(CultureInfo.InvariantCulture);
                    if (!seen.Add(identity))
                        throw new FormatException("MultiEdu Provider ID 重复: " + identity);
                    if ((definition.IPv4 == null) == (definition.IPv6 == null))
                        throw new FormatException(identity +
                            " 必须且只能包含一个 ipv4 或 ipv6 实现");

                    EndpointDefinition endpoint = definition.IPv4 ?? definition.IPv6;
                    string toolTip;
                    catalog.ToolTips.TryGetValue(group +
                        definition.ID.ToString("00", CultureInfo.InvariantCulture), out toolTip);
                    if (toolTip == null)
                        catalog.ToolTips.TryGetValue(group +
                            definition.ID.ToString(CultureInfo.InvariantCulture), out toolTip);
                    staged.Add(new MultiEduApiProvider
                    {
                        Group = group,
                        ID = definition.ID,
                        Name = endpoint.Name,
                        ToolTip = toolTip,
                        GetIP = token => ExecuteAsync(endpoint, token)
                    });
                }

                if (staged.Count == 0)
                    throw new FormatException("配置文件中没有 MULTIEDU Provider");

                staged.Sort((left, right) =>
                {
                    int groupOrder = string.Compare(left.Group, right.Group,
                        StringComparison.OrdinalIgnoreCase);
                    return groupOrder != 0 ? groupOrder : left.ID.CompareTo(right.ID);
                });
                foreach (MultiEduApiProvider provider in staged) providers.Add(provider);
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                TryWriteMultiEduError(path, ex);
                errorMessage = "教育网 API 配置加载失败：\r\n" + ex.Message +
                    "\r\n\r\n详细信息已写入 NICX_MultiEdu.error.log。";
                return false;
            }
        }

        private static void TryWriteMultiEduError(string configPath, Exception ex)
        {
            try
            {
                string errorPath = Path.Combine(
                    Path.GetDirectoryName(configPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    "NICX_MultiEdu.error.log");
                File.WriteAllText(errorPath,
                    "NICX_MultiEdu.nicxapi 加载失败\r\n" +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\r\n\r\n" + ex,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        internal static void WriteMultiEduWarning(string warning)
        {
            try
            {
                string assemblyPath = typeof(MultiEdu).Assembly.Location;
                string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : Path.GetDirectoryName(assemblyPath);
                string errorPath = Path.Combine(
                    baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                    "NICX_MultiEdu.error.log");
                File.WriteAllText(errorPath,
                    "NICX_MultiEdu.nicxapi 加载警告\r\n" +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\r\n\r\n" + warning,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        internal static bool TryLoadMultiFull(
            IList<MultiFullApiProvider> providers,
            out string errorMessage)
        {
            string assemblyPath = typeof(MultiFull).Assembly.Location;
            string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyPath);
            string path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                MultiFullFileName);

            if (!File.Exists(path))
            {
                errorMessage = "未找到多出口全能版 API 配置文件：\r\n" + path;
                return false;
            }

            try
            {
                string source = File.ReadAllText(path, new UTF8Encoding(false, true));
                CatalogDefinition catalog = new Parser(source).Parse();
                ValidateCatalog(catalog);
                var staged = new List<MultiFullApiProvider>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ProviderDefinition definition in catalog.Providers)
                {
                    string group = definition.Group.ToUpperInvariant();
                    if (group != "MULTIFULLCN" && group != "MULTIFULLGFW" &&
                        group != "MULTIFULLDUAL")
                        throw new FormatException("未知 MultiFull Provider 分组: " + definition.Group);
                    if (!string.IsNullOrWhiteSpace(definition.GeoReference))
                        throw new FormatException(group + " Provider 不支持 use");
                    if (definition.ID < 1)
                        throw new FormatException(group + " Provider ID 必须大于 0: " + definition.ID);
                    string identity = group + ":" + definition.ID.ToString(CultureInfo.InvariantCulture);
                    if (!seen.Add(identity))
                        throw new FormatException("MultiFull Provider ID 重复: " + identity);
                    if ((definition.IPv4 == null) == (definition.IPv6 == null))
                        throw new FormatException(identity + " 必须且只能包含一个 ipv4 或 ipv6 实现");

                    EndpointDefinition endpoint = definition.IPv4 ?? definition.IPv6;
                    string toolTip;
                    catalog.ToolTips.TryGetValue(group +
                        definition.ID.ToString("00", CultureInfo.InvariantCulture), out toolTip);
                    if (toolTip == null)
                        catalog.ToolTips.TryGetValue(group +
                            definition.ID.ToString(CultureInfo.InvariantCulture), out toolTip);
                    staged.Add(new MultiFullApiProvider
                    {
                        Group = group,
                        ID = definition.ID,
                        Name = endpoint.Name,
                        ToolTip = toolTip,
                        GetIP = (token, setTitle) => ExecuteAsync(endpoint, token, setTitle)
                    });
                }

                if (staged.Count == 0)
                    throw new FormatException("配置文件中没有 MultiFull Provider");
                staged.Sort((left, right) =>
                {
                    int groupOrder = string.Compare(left.Group, right.Group,
                        StringComparison.OrdinalIgnoreCase);
                    return groupOrder != 0 ? groupOrder : left.ID.CompareTo(right.ID);
                });
                foreach (MultiFullApiProvider provider in staged) providers.Add(provider);
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                WriteMultiFullLog(path, "加载失败", ex.ToString());
                errorMessage = "多出口全能版 API 配置加载失败：\r\n" + ex.Message +
                    "\r\n\r\n详细信息已写入 NICX_MultiFull.error.log。";
                return false;
            }
        }

        internal static bool TryLoadMultiLite(
            IList<MultiLiteApiProvider> providers,
            out string errorMessage)
        {
            string assemblyPath = typeof(MultiLite).Assembly.Location;
            string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyPath);
            string path = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                MultiLiteFileName);

            if (!File.Exists(path))
            {
                errorMessage = "未找到多出口精简版 API 配置文件：\r\n" + path;
                return false;
            }

            try
            {
                string source = File.ReadAllText(path, new UTF8Encoding(false, true));
                CatalogDefinition catalog = new Parser(source).Parse();
                ValidateCatalog(catalog);
                var staged = new List<MultiLiteApiProvider>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ProviderDefinition definition in catalog.Providers)
                {
                    string group = definition.Group.ToUpperInvariant();
                    if (group != "MULTILITEIP" && group != "MULTILITEISP" &&
                        group != "MULTILITEMISC")
                        throw new FormatException("未知 MultiLite Provider 分组: " + definition.Group);
                    if (!string.IsNullOrWhiteSpace(definition.GeoReference))
                        throw new FormatException("MULTILITE Provider 不支持 use");
                    if (definition.ID < 1)
                        throw new FormatException("MULTILITE Provider ID 必须大于 0: " + definition.ID);
                    if ((definition.IPv4 == null) == (definition.IPv6 == null))
                        throw new FormatException(group + ":" + definition.ID +
                            " 必须且只能包含一个 ipv4 或 ipv6 实现");

                    string identity = group + ":" +
                        definition.ID.ToString(CultureInfo.InvariantCulture);
                    if (!seen.Add(identity))
                        throw new FormatException("MultiLite Provider ID 重复: " + identity);

                    EndpointDefinition endpoint = definition.IPv4 ?? definition.IPv6;
                    string toolTip;
                    catalog.ToolTips.TryGetValue(group +
                        definition.ID.ToString("00", CultureInfo.InvariantCulture), out toolTip);
                    if (toolTip == null)
                        catalog.ToolTips.TryGetValue(group +
                            definition.ID.ToString(CultureInfo.InvariantCulture), out toolTip);
                    staged.Add(new MultiLiteApiProvider
                    {
                        Group = group,
                        ID = definition.ID,
                        Name = endpoint.Name,
                        ToolTip = toolTip,
                        GetIP = (localIP, token, setTitle) =>
                            ExecuteAsync(endpoint, token, setTitle, localIP)
                    });
                }

                if (staged.Count == 0)
                    throw new FormatException("配置文件中没有 MULTILITE Provider");
                staged.Sort((left, right) =>
                {
                    int groupOrder = string.Compare(left.Group, right.Group,
                        StringComparison.OrdinalIgnoreCase);
                    return groupOrder != 0 ? groupOrder : left.ID.CompareTo(right.ID);
                });
                foreach (MultiLiteApiProvider provider in staged) providers.Add(provider);
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                WriteMultiLiteLog(path, "加载失败", ex.ToString());
                errorMessage = "多出口精简版 API 配置加载失败：\r\n" + ex.Message +
                    "\r\n\r\n详细信息已写入 NICX_MultiLite.error.log。";
                return false;
            }
        }

        internal static void WriteMultiLiteWarning(string warning)
        {
            string assemblyPath = typeof(MultiLite).Assembly.Location;
            string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyPath);
            WriteMultiLiteLog(Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                MultiLiteFileName), "加载警告", warning);
        }

        private static void WriteMultiLiteLog(string configPath, string heading, string details)
        {
            try
            {
                string errorPath = Path.Combine(
                    Path.GetDirectoryName(configPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    "NICX_MultiLite.error.log");
                File.WriteAllText(errorPath,
                    "NICX_MultiLite.nicxapi " + heading + "\r\n" +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\r\n\r\n" + details,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        internal static void WriteMultiFullWarning(string warning)
        {
            string assemblyPath = typeof(MultiFull).Assembly.Location;
            string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyPath);
            WriteMultiFullLog(Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                MultiFullFileName), "加载警告", warning);
        }

        private static void WriteMultiFullLog(string configPath, string heading, string details)
        {
            try
            {
                string errorPath = Path.Combine(
                    Path.GetDirectoryName(configPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    "NICX_MultiFull.error.log");
                File.WriteAllText(errorPath,
                    "NICX_MultiFull.nicxapi " + heading + "\r\n" +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\r\n\r\n" + details,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static void AppendGeoProviders(
            IList<GeoProvider> destination, IList<GeoProvider> source)
        {
            var ids = new HashSet<int>();
            foreach (GeoProvider provider in destination) ids.Add(provider.ID);
            foreach (GeoProvider provider in source)
                if (ids.Add(provider.ID)) destination.Add(provider);
        }

        private static void TryWriteGeoError(string configPath, Exception ex)
        {
            try
            {
                string errorPath = Path.Combine(
                    Path.GetDirectoryName(configPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    "NICX_Api2.error.log");
                File.WriteAllText(errorPath,
                    "NICX_Api2.nicxapi 加载失败\r\n" +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\r\n\r\n" + ex,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static void AppendProviders(
            IList<ApiProvider> destination,
            IList<ApiProvider> source)
        {
            var ids = new HashSet<int>();
            foreach (ApiProvider provider in destination) ids.Add(provider.ID);
            foreach (ApiProvider provider in source)
                if (ids.Add(provider.ID)) destination.Add(provider);
        }

        private static void TryWriteError(string configPath, Exception ex)
        {
            try
            {
                string errorPath = Path.Combine(
                    Path.GetDirectoryName(configPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    "NICX_Api1.error.log");
                File.WriteAllText(errorPath,
                    "NICX_Api1.nicxapi 加载失败\r\n" +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "\r\n\r\n" + ex,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static readonly HashSet<string> SupportedFunctions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // 当前推荐名称。
                "ExtractIP", "ValidateIP", "GetJson", "GetKeysJson",
                "GetMidText", "ReplaceText", "GetLeftText", "GetRightText",
                "DelLeftText", "DelRightText", "DecodeUnicode", "Trim", "TrimAll",
                "Concat", "If", "IsIPv4", "IsIPv6", "IsEmpty", "Contains",
                "HasPrefix", "Or", "And", "Not", "MatchRegex", "Equals",
                "ChooseRandom", "CheckJson", "GetLineValue", "GetIni",
                "MaskLastIPv4", "EncodeUrl", "SelectMaxJson", "GetCache", "SetCache",
                "CleanJson", "MessageBox", "Debug"
            };

        private static readonly HashSet<string> SupportedRequestProperties =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "url", "method", "postData", "useCurlUA", "useRandomUA", "encoding",
                "forceIPv4", "forceIPv6", "responseHeader"
                , "timeoutMs", "cookieWarmup", "cookieHost", "ensureCookieName",
                "attempts", "successJsonPath"
            };

        private static void ValidateCatalog(CatalogDefinition catalog)
        {
            foreach (ProviderDefinition provider in catalog.Providers)
            {
                ValidateEndpoint(provider.IPv4);
                ValidateEndpoint(provider.IPv6);
            }
            foreach (EndpointDefinition endpoint in catalog.GeoEndpoints.Values)
                ValidateEndpoint(endpoint);
        }

        private static void ValidateEndpoint(EndpointDefinition endpoint)
        {
            if (endpoint == null) return;
            bool hasReturn = false;
            foreach (Statement statement in endpoint.Statements)
            {
                var request = statement as RequestStatement;
                if (request != null)
                {
                    if (!request.Properties.ContainsKey("url"))
                        throw new FormatException(endpoint.Name + " 的 request 缺少 url");
                    foreach (KeyValuePair<string, Expression> pair in request.Properties)
                    {
                        if (!SupportedRequestProperties.Contains(pair.Key))
                            throw new FormatException(endpoint.Name + " 不支持 request 属性: " + pair.Key);
                        ValidateExpression(pair.Value);
                    }
                    foreach (Expression value in request.Headers.Values) ValidateExpression(value);
                    ValidateExpression(request.Condition);
                    continue;
                }

                var let = statement as LetStatement;
                if (let != null)
                {
                    ValidateExpression(let.Expression);
                    continue;
                }

                var title = statement as SetTitleStatement;
                if (title != null)
                {
                    ValidateExpression(title.Condition);
                    ValidateExpression(title.Expression);
                    continue;
                }

                var conditional = statement as ConditionalReturnStatement;
                if (conditional != null)
                {
                    ValidateExpression(conditional.Condition);
                    ValidateExpression(conditional.Expression);
                    continue;
                }

                var result = statement as ReturnStatement;
                if (result != null)
                {
                    ValidateExpression(result.Expression);
                    hasReturn = true;
                }

                var geoResult = statement as ReturnGeoStatement;
                if (geoResult != null)
                {
                    ValidateExpression(geoResult.Condition);
                    ValidateExpression(geoResult.Location);
                    ValidateExpression(geoResult.AS);
                    hasReturn = true;
                }
            }
            if (!hasReturn) throw new FormatException(endpoint.Name + " 缺少最终 return");
        }

        private static void ValidateExpression(Expression expression)
        {
            if (expression == null) return;
            var call = expression as CallExpression;
            if (call == null) return;
            string name = call.Name;
            if (!SupportedFunctions.Contains(name))
                throw new FormatException("不支持的方法: " + call.Name);
            foreach (Expression argument in call.Arguments) ValidateExpression(argument);
        }

        private static async Task<GeoResult> ExecuteGeoAsync(
            EndpointDefinition endpoint, string ip, CancellationToken token)
        {
            var variables = CreateVariables();
            variables["ip"] = ip ?? string.Empty;

            foreach (Statement statement in endpoint.Statements)
            {
                token.ThrowIfCancellationRequested();
                var request = statement as RequestStatement;
                if (request != null)
                {
                    if (request.Condition == null || ToBool(Evaluate(request.Condition, variables)))
                        variables[request.Variable] = await ExecuteRequestAsync(request, variables, token);
                    continue;
                }
                var let = statement as LetStatement;
                if (let != null)
                {
                    variables[let.Variable] = Evaluate(let.Expression, variables);
                    continue;
                }
                var geo = statement as ReturnGeoStatement;
                if (geo != null && (geo.Condition == null || ToBool(Evaluate(geo.Condition, variables))))
                {
                    return new GeoResult
                    {
                        Loc = ToText(Evaluate(geo.Location, variables)),
                        AS = ToText(Evaluate(geo.AS, variables))
                    };
                }
            }
            throw new InvalidOperationException("geo 方法 " + endpoint.Name + " 没有 returnGeo 语句");
        }

        private static Dictionary<string, object> CreateVariables()
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "null", null }, { "true", true }, { "false", false }
            };
        }

        private static async Task<string> ExecuteAsync(
            EndpointDefinition endpoint,
            CancellationToken token,
            Action<string> setTitle = null,
            IPAddress bindIP = null)
        {
            var variables = CreateVariables();

            foreach (Statement statement in endpoint.Statements)
            {
                token.ThrowIfCancellationRequested();

                var request = statement as RequestStatement;
                if (request != null)
                {
                    if (request.Condition == null || ToBool(Evaluate(request.Condition, variables)))
                        variables[request.Variable] = await ExecuteRequestAsync(
                            request, variables, token, bindIP);
                    continue;
                }

                var let = statement as LetStatement;
                if (let != null)
                {
                    variables[let.Variable] = Evaluate(let.Expression, variables);
                    continue;
                }

                var title = statement as SetTitleStatement;
                if (title != null && (title.Condition == null ||
                    ToBool(Evaluate(title.Condition, variables))))
                {
                    if (setTitle != null)
                        setTitle(ToText(Evaluate(title.Expression, variables)));
                    continue;
                }

                var result = statement as ReturnStatement;
                if (result != null)
                {
                    object value = Evaluate(result.Expression, variables);
                    return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }

                var conditionalResult = statement as ConditionalReturnStatement;
                if (conditionalResult != null &&
                    ToBool(Evaluate(conditionalResult.Condition, variables)))
                {
                    object value = Evaluate(conditionalResult.Expression, variables);
                    return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }

            throw new InvalidOperationException("接口 " + endpoint.Name + " 没有 return 语句");
        }

        private static async Task<string> ExecuteRequestAsync(
            RequestStatement request,
            IDictionary<string, object> variables,
            CancellationToken token,
            IPAddress bindIP = null)
        {
            string url = GetStringProperty(request, "url", variables, true);
            string methodText = GetStringProperty(request, "method", variables, false) ?? "GET";
            string postData = GetStringProperty(request, "postData", variables, false);
            string encodingName = GetStringProperty(request, "encoding", variables, false);
            string responseHeader = GetStringProperty(request, "responseHeader", variables, false);
            string cookieWarmup = GetStringProperty(request, "cookieWarmup", variables, false);
            string cookieHost = GetStringProperty(request, "cookieHost", variables, false);
            string ensureCookieName = GetStringProperty(request, "ensureCookieName", variables, false);
            string successJsonPath = GetStringProperty(request, "successJsonPath", variables, false);

            bool useCurlUA = GetBoolProperty(request, "useCurlUA", variables, false);
            bool useRandomUA = GetBoolProperty(request, "useRandomUA", variables, true);
            bool forceIPv4 = GetBoolProperty(request, "forceIPv4", variables, false);
            bool forceIPv6 = GetBoolProperty(request, "forceIPv6", variables, false);
            int timeoutMs = GetIntProperty(request, "timeoutMs", variables, 0);
            int attempts = Math.Max(1, GetIntProperty(request, "attempts", variables, 1));

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Expression> pair in request.Headers)
            {
                object value = Evaluate(pair.Value, variables);
                headers[pair.Key] = value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            Encoding encoding = null;
            if (!string.IsNullOrWhiteSpace(encodingName))
                encoding = Encoding.GetEncoding(encodingName);

            HttpMethod method = string.Equals(methodText, "POST", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Post
                : HttpMethod.Get;

            CancellationTokenSource timeout = null;
            CancellationTokenSource linked = null;
            CancellationToken effectiveToken = token;
            try
            {
                if (timeoutMs > 0)
                {
                    timeout = new CancellationTokenSource(timeoutMs);
                    linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                    effectiveToken = linked.Token;
                }

                if (!string.IsNullOrWhiteSpace(cookieWarmup))
                {
                    var cookies = new CookieContainer();
                    Uri host = new Uri(string.IsNullOrWhiteSpace(cookieHost) ? url : cookieHost);
                    string last = string.Empty;
                    for (int attempt = 0; attempt < attempts; attempt++)
                    {
                        effectiveToken.ThrowIfCancellationRequested();
                        await HttpHelper.SendWithCookiesAsync(
                            cookieWarmup, effectiveToken, cookies, bindIP: bindIP);
                        if (!string.IsNullOrWhiteSpace(ensureCookieName) &&
                            cookies.GetCookies(host)[ensureCookieName] == null)
                        {
                            cookies.Add(host, new Cookie(ensureCookieName,
                                Guid.NewGuid().ToString("D").ToLowerInvariant()));
                        }
                        last = await HttpHelper.SendWithCookiesAsync(
                            url, effectiveToken, cookies, method, postData,
                            headers.Count == 0 ? null : headers, useRandomUA,
                            bindIP: bindIP);
                        if (string.IsNullOrWhiteSpace(successJsonPath) ||
                            string.Equals(TextHelper.ExtractJsonValue(last, successJsonPath),
                                "true", StringComparison.OrdinalIgnoreCase))
                            return last;
                    }
                    return last;
                }

                return await HttpHelper.SendAsync(
                    url, effectiveToken, method, postData,
                    headers.Count == 0 ? null : headers,
                    useCurlUA, useRandomUA, encoding, forceIPv4, forceIPv6,
                    bindIP, responseHeader);
            }
            finally
            {
                if (linked != null) linked.Dispose();
                if (timeout != null) timeout.Dispose();
            }
        }

        private static string GetStringProperty(
            RequestStatement request,
            string name,
            IDictionary<string, object> variables,
            bool required)
        {
            Expression expression;
            if (!request.Properties.TryGetValue(name, out expression))
            {
                if (required) throw new FormatException("request 缺少 " + name);
                return null;
            }
            object value = Evaluate(expression, variables);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBoolProperty(
            RequestStatement request,
            string name,
            IDictionary<string, object> variables,
            bool defaultValue)
        {
            Expression expression;
            if (!request.Properties.TryGetValue(name, out expression)) return defaultValue;
            return ToBool(Evaluate(expression, variables));
        }

        private static int GetIntProperty(
            RequestStatement request, string name,
            IDictionary<string, object> variables, int defaultValue)
        {
            Expression expression;
            if (!request.Properties.TryGetValue(name, out expression)) return defaultValue;
            return ToInt(Evaluate(expression, variables));
        }

        private static object Evaluate(Expression expression, IDictionary<string, object> variables)
        {
            var literal = expression as LiteralExpression;
            if (literal != null) return literal.Value;

            var variable = expression as VariableExpression;
            if (variable != null)
            {
                object value;
                if (variables.TryGetValue(variable.Name, out value)) return value;
                // GET/POST 等枚举式参数按字符串处理。
                return variable.Name;
            }

            var call = expression as CallExpression;
            if (call == null) throw new InvalidOperationException("未知表达式");

            var args = new List<object>(call.Arguments.Count);
            foreach (Expression argument in call.Arguments)
                args.Add(Evaluate(argument, variables));

            string name = call.Name;
            switch (name)
            {
                case "ExtractIP":
                    RequireArgs(name, args, 2);
                    return TextHelper.UniversalExtractIP(ToText(args[0]), ToBool(args[1]));
                case "ValidateIP":
                    RequireArgs(name, args, 1);
                    return TextHelper.UniversalValidateIP(ToText(args[0]));
                case "GetJson":
                    RequireArgs(name, args, 2);
                    return TextHelper.ExtractJsonValue(ToText(args[0]), ToText(args[1]));
                case "GetKeysJson":
                    if (args.Count < 2) throw new FormatException(name + " 至少需要 2 个参数");
                    var keys = new string[args.Count - 1];
                    for (int i = 1; i < args.Count; i++) keys[i - 1] = ToText(args[i]);
                    return TextHelper.ExtractJsonValueByKeys(ToText(args[0]), keys);
                case "GetMidText":
                    RequireArgs(name, args, 3);
                    return TextHelper.GetMidText(ToText(args[0]), ToText(args[1]), ToText(args[2]));
                case "ReplaceText":
                    if (args.Count < 3 || args.Count % 2 == 0)
                        throw new FormatException(name +
                            " 需要 text 及一组或多组 old/new 参数，实际为 " + args.Count);
                    string replaced = ToText(args[0]);
                    for (int i = 1; i < args.Count; i += 2)
                        replaced = TextHelper.ReplaceText(replaced, ToText(args[i]), ToText(args[i + 1]));
                    return replaced;
                case "GetLeftText":
                    RequireArgs(name, args, 2);
                    return TextHelper.GetLeftText(ToText(args[0]), ToText(args[1]));
                case "GetRightText":
                    RequireArgs(name, args, 2);
                    return TextHelper.GetRightText(ToText(args[0]), ToText(args[1]));
                case "DelLeftText":
                    RequireArgs(name, args, 2);
                    return TextHelper.StrDeleteLeft(ToText(args[0]), ToInt(args[1]));
                case "DelRightText":
                    RequireArgs(name, args, 2);
                    return TextHelper.StrDeleteRight(ToText(args[0]), ToInt(args[1]));
                case "DecodeUnicode":
                    RequireArgs(name, args, 1);
                    return TextHelper.UnescapeUnicode(ToText(args[0]));
                case "Trim":
                    RequireArgs(name, args, 1);
                    return ToText(args[0]).Trim();
                case "TrimAll":
                    RequireArgs(name, args, 1);
                    return Regex.Replace(ToText(args[0]), @"\s+", string.Empty);
                case "Concat":
                    var builder = new StringBuilder();
                    foreach (object value in args) builder.Append(ToText(value));
                    return builder.ToString();
                case "If":
                    RequireArgs(name, args, 3);
                    return ToBool(args[0]) ? args[1] : args[2];
                case "IsIPv4":
                    RequireArgs(name, args, 1);
                    return TextHelper.IsValidIPv4(ToText(args[0]));
                case "IsIPv6":
                    RequireArgs(name, args, 1);
                    IPAddress address;
                    return IPAddress.TryParse(ToText(args[0]), out address) &&
                        address.AddressFamily == AddressFamily.InterNetworkV6;
                case "IsEmpty":
                    RequireArgs(name, args, 1);
                    return string.IsNullOrWhiteSpace(args[0] == null ? null : ToText(args[0]));
                case "Contains":
                    RequireArgs(name, args, 2);
                    return ToText(args[0]).Contains(ToText(args[1]));
                case "HasPrefix":
                    RequireArgs(name, args, 2);
                    return ToText(args[0]).StartsWith(ToText(args[1]), StringComparison.Ordinal);
                case "Or":
                    RequireArgs(name, args, 2);
                    return ToBool(args[0]) || ToBool(args[1]);
                case "And":
                    RequireArgs(name, args, 2);
                    return ToBool(args[0]) && ToBool(args[1]);
                case "Not":
                    RequireArgs(name, args, 1);
                    return !ToBool(args[0]);
                case "MatchRegex":
                    RequireArgs(name, args, 2);
                    Match match = Regex.Match(ToText(args[0]), ToText(args[1]));
                    return match.Success ? match.Value : string.Empty;
                case "Equals":
                    RequireArgs(name, args, 2);
                    return string.Equals(ToText(args[0]), ToText(args[1]),
                        StringComparison.OrdinalIgnoreCase);
                case "ChooseRandom":
                    if (args.Count == 0) return string.Empty;
                    lock (ScriptRandom) return ToText(args[ScriptRandom.Next(args.Count)]);
                case "CheckJson":
                    if (args.Count < 3)
                        throw new FormatException(name + " 至少需要 json、text、path 三个参数");
                    for (int i = 2; i < args.Count; i++)
                    {
                        string special = TextHelper.ExtractJsonValue(
                            ToText(args[0]), ToText(args[i])).Trim();
                        if (special == "1" || string.Equals(special, "true",
                            StringComparison.OrdinalIgnoreCase))
                            return ToText(args[1]);
                    }
                    return string.Empty;
                case "GetLineValue":
                    RequireArgs(name, args, 2);
                    return GetValueByLine(ToText(args[0]), ToText(args[1]));
                case "GetIni":
                    if (args.Count == 2)
                        return GetIniValue(ToText(args[0]), null, ToText(args[1]), true);
                    if (args.Count == 3)
                        return GetIniValue(ToText(args[0]), ToText(args[1]), ToText(args[2]), false);
                    throw new FormatException(name + " 需要 2 或 3 个参数，实际为 " + args.Count);
                case "MaskLastIPv4":
                    RequireArgs(name, args, 1);
                    string ip = ToText(args[0]);
                    int lastDot = ip.LastIndexOf('.');
                    return lastDot > 0 && !ip.Contains(":") ? ip.Substring(0, lastDot) + ".0" : string.Empty;
                case "EncodeUrl":
                    RequireArgs(name, args, 1);
                    return Uri.EscapeDataString(ToText(args[0]));
                case "SelectMaxJson":
                    return SelectHighestJson(args);
                case "GetCache":
                    RequireArgs(name, args, 1);
                    return GetGeoCache(ToText(args[0]));
                case "SetCache":
                    RequireArgs(name, args, 3);
                    if (!string.IsNullOrWhiteSpace(ToText(args[1])))
                        SetGeoCache(ToText(args[0]), ToText(args[1]), ToInt(args[2]));
                    return args[1];
                case "CleanJson":
                    RequireArgs(name, args, 1);
                    string clean = ToText(args[0]);
                    if (clean.StartsWith("\ufeff", StringComparison.Ordinal)) clean = clean.Substring(1);
                    return clean.Trim();
                case "MessageBox":
                    RequireArgs(name, args, 1);
                    string message = ToText(args[0]);
                    MessageBox.Show(message, "NICX API调试输出",
                        MessageBoxButtons.OK);
                    return message;
                case "Debug":
                    RequireArgs(name, args, 3);
                    string debugText = ToText(args[0]);
                    WriteDebugText(debugText, ToText(args[1]), ToText(args[2]));
                    return debugText;
                default:
                    throw new FormatException("不支持的方法: " + call.Name);
            }
        }

        private static string GetValueByLine(string fullText, string key)
        {
            if (string.IsNullOrEmpty(fullText) || string.IsNullOrEmpty(key)) return string.Empty;
            string search = key + ":";
            int start = fullText.IndexOf(search, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += search.Length;
            int end = fullText.IndexOf('\n', start);
            return (end < 0 ? fullText.Substring(start) : fullText.Substring(start, end - start)).Trim();
        }

        private static string GetIniValue(
            string text, string section, string key, bool searchAllSections)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            string currentSection = string.Empty;
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (i == 0 && line.StartsWith("\ufeff", StringComparison.Ordinal))
                    line = line.Substring(1);

                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (trimmed.Length >= 2 && trimmed[0] == '[' &&
                    trimmed[trimmed.Length - 1] == ']')
                {
                    currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    continue;
                }

                if (!searchAllSections && !string.Equals(
                    currentSection, section ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    continue;

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0) continue;
                string foundKey = line.Substring(0, equalsIndex).Trim();
                if (string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(equalsIndex + 1).Trim();
            }
            return string.Empty;
        }

        private static string SelectHighestJson(IList<object> args)
        {
            if (args.Count < 3 || args.Count % 2 == 0)
                throw new FormatException("SelectMaxJson 需要 JSON 及若干名称/路径对");
            string json = ToText(args[0]);
            double best = double.MinValue;
            var candidates = new List<string>();
            for (int i = 1; i < args.Count; i += 2)
            {
                string candidate = ToText(args[i]);
                string raw = TextHelper.ExtractJsonValue(json, ToText(args[i + 1]));
                double value;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) continue;
                if (value > best)
                {
                    best = value;
                    candidates.Clear();
                    candidates.Add(candidate);
                }
                else if (value == best) candidates.Add(candidate);
            }
            if (candidates.Count > 1 && candidates.Contains("api")) candidates.Remove("api");
            if (candidates.Count == 0) return string.Empty;
            lock (ScriptRandom) return candidates[ScriptRandom.Next(candidates.Count)];
        }

        private static string GetGeoCache(string key)
        {
            lock (GeoCacheLock)
            {
                CachedGeoValue value;
                if (!GeoCache.TryGetValue(key, out value)) return string.Empty;
                if (value.ExpiresUtc <= DateTime.UtcNow)
                {
                    GeoCache.Remove(key);
                    return string.Empty;
                }
                return value.Value;
            }
        }

        private static void SetGeoCache(string key, string value, int minutes)
        {
            lock (GeoCacheLock)
                GeoCache[key] = new CachedGeoValue
                {
                    Value = value,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, minutes))
                };
        }

        private static void WriteDebugText(string text, string name, string place)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(place))
            {
                try
                {
                    string safeName = Path.GetFileNameWithoutExtension(Path.GetFileName(name));
                    if (!string.IsNullOrWhiteSpace(safeName) && Directory.Exists(place))
                    {
                        File.WriteAllText(Path.Combine(place, safeName + ".txt"),
                            text ?? string.Empty, new UTF8Encoding(false));
                        return;
                    }
                }
                catch { }
            }

            try
            {
                string assemblyPath = typeof(NicxApi1Script).Assembly.Location;
                string baseDirectory = string.IsNullOrWhiteSpace(assemblyPath)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : Path.GetDirectoryName(assemblyPath);
                string fileName = "NICX_Temp_" +
                    DateTime.Now.ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt";
                File.WriteAllText(Path.Combine(
                    baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, fileName),
                    text ?? string.Empty, new UTF8Encoding(false));
            }
            catch { }
        }

        private static void RequireArgs(string name, IList<object> args, int count)
        {
            if (args.Count != count)
                throw new FormatException(name + " 需要 " + count + " 个参数，实际为 " + args.Count);
        }

        private static string ToText(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool ToBool(object value)
        {
            if (value is bool) return (bool)value;
            bool result;
            return bool.TryParse(ToText(value), out result) && result;
        }

        private static int ToInt(object value)
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private sealed class CatalogDefinition
        {
            internal readonly List<ProviderDefinition> Providers = new List<ProviderDefinition>();
            internal readonly Dictionary<string, string> ToolTips =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, EndpointDefinition> GeoEndpoints =
                new Dictionary<string, EndpointDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ProviderDefinition
        {
            internal string Group;
            internal int ID;
            internal EndpointDefinition IPv4;
            internal EndpointDefinition IPv6;
            internal string GeoReference;
        }

        private sealed class EndpointDefinition
        {
            internal string Name;
            internal readonly List<Statement> Statements = new List<Statement>();
        }

        private abstract class Statement { }

        private sealed class RequestStatement : Statement
        {
            internal string Variable;
            internal Expression Condition;
            internal readonly Dictionary<string, Expression> Properties =
                new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, Expression> Headers =
                new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class LetStatement : Statement
        {
            internal string Variable;
            internal Expression Expression;
        }

        private sealed class ReturnStatement : Statement
        {
            internal Expression Expression;
        }

        private sealed class ConditionalReturnStatement : Statement
        {
            internal Expression Condition;
            internal Expression Expression;
        }

        private sealed class ReturnGeoStatement : Statement
        {
            internal Expression Condition;
            internal Expression Location;
            internal Expression AS;
        }

        private sealed class SetTitleStatement : Statement
        {
            internal Expression Condition;
            internal Expression Expression;
        }

        private abstract class Expression { }

        private sealed class LiteralExpression : Expression
        {
            internal object Value;
        }

        private sealed class VariableExpression : Expression
        {
            internal string Name;
        }

        private sealed class CallExpression : Expression
        {
            internal string Name;
            internal readonly List<Expression> Arguments = new List<Expression>();
        }

        private enum TokenKind
        {
            Identifier,
            Number,
            String,
            Symbol,
            End
        }

        private sealed class Token
        {
            internal TokenKind Kind;
            internal string Text;
            internal int Line;
            internal int Column;
        }

        private sealed class Lexer
        {
            private readonly string _source;
            private int _index;
            private int _line = 1;
            private int _column = 1;

            internal Lexer(string source)
            {
                _source = source ?? string.Empty;
            }

            internal Token Next()
            {
                SkipTrivia();
                if (_index >= _source.Length)
                    return NewToken(TokenKind.End, string.Empty, _line, _column);

                int line = _line;
                int column = _column;
                char current = _source[_index];

                if (char.IsLetter(current) || current == '_')
                {
                    var builder = new StringBuilder();
                    while (_index < _source.Length)
                    {
                        char c = _source[_index];
                        if (!char.IsLetterOrDigit(c) && c != '_' && c != '.') break;
                        builder.Append(c);
                        Advance(c);
                    }
                    return NewToken(TokenKind.Identifier, builder.ToString(), line, column);
                }

                if (char.IsDigit(current))
                {
                    var builder = new StringBuilder();
                    while (_index < _source.Length && char.IsDigit(_source[_index]))
                    {
                        builder.Append(_source[_index]);
                        Advance(_source[_index]);
                    }
                    return NewToken(TokenKind.Number, builder.ToString(), line, column);
                }

                if (current == '"')
                    return ReadString(line, column);

                Advance(current);
                return NewToken(TokenKind.Symbol, current.ToString(), line, column);
            }

            private Token ReadString(int line, int column)
            {
                bool triple = _index + 2 < _source.Length &&
                    _source[_index] == '"' && _source[_index + 1] == '"' && _source[_index + 2] == '"';

                if (triple)
                {
                    Advance('"'); Advance('"'); Advance('"');
                    var raw = new StringBuilder();
                    while (_index < _source.Length)
                    {
                        if (_index + 2 < _source.Length &&
                            _source[_index] == '"' && _source[_index + 1] == '"' && _source[_index + 2] == '"')
                        {
                            Advance('"'); Advance('"'); Advance('"');
                            return NewToken(TokenKind.String, raw.ToString(), line, column);
                        }
                        char c = _source[_index];
                        raw.Append(c);
                        Advance(c);
                    }
                    throw Error("未结束的三引号字符串", line, column);
                }

                Advance('"');
                var builder = new StringBuilder();
                while (_index < _source.Length)
                {
                    char c = _source[_index];
                    Advance(c);
                    if (c == '"') return NewToken(TokenKind.String, builder.ToString(), line, column);
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }
                    if (_index >= _source.Length) break;
                    char escaped = _source[_index];
                    Advance(escaped);
                    switch (escaped)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case '\\': builder.Append('\\'); break;
                        case '"': builder.Append('"'); break;
                        default: builder.Append(escaped); break;
                    }
                }
                throw Error("未结束的字符串", line, column);
            }

            private void SkipTrivia()
            {
                while (_index < _source.Length)
                {
                    char c = _source[_index];
                    if (char.IsWhiteSpace(c))
                    {
                        Advance(c);
                        continue;
                    }
                    if (c == '/' && _index + 1 < _source.Length && _source[_index + 1] == '/')
                    {
                        while (_index < _source.Length && _source[_index] != '\n') Advance(_source[_index]);
                        continue;
                    }
                    break;
                }
            }

            private void Advance(char c)
            {
                _index++;
                if (c == '\n')
                {
                    _line++;
                    _column = 1;
                }
                else _column++;
            }

            private static Token NewToken(TokenKind kind, string text, int line, int column)
            {
                return new Token { Kind = kind, Text = text, Line = line, Column = column };
            }

            private static FormatException Error(string message, int line, int column)
            {
                return new FormatException(message + "，第 " + line + " 行，第 " + column + " 列");
            }
        }

        private sealed class Parser
        {
            private readonly Lexer _lexer;
            private Token _current;

            internal Parser(string source)
            {
                _lexer = new Lexer(source);
                _current = _lexer.Next();
            }

            internal CatalogDefinition Parse()
            {
                var catalog = new CatalogDefinition();
                ExpectIdentifier("nicxapi");
                int version = ReadNumber();
                if (version != 1) throw Error("不支持的 nicxapi 版本: " + version);
                ExpectSymbol(";");

                while (_current.Kind != TokenKind.End)
                {
                    if (IsIdentifier("tooltip")) ParseToolTip(catalog);
                    else if (IsIdentifier("provider")) catalog.Providers.Add(ParseProvider());
                    else if (IsIdentifier("geo")) ParseGeo(catalog);
                    else throw Error("期待 tooltip、geo 或 provider");
                }
                return catalog;
            }

            private void ParseGeo(CatalogDefinition catalog)
            {
                ExpectIdentifier("geo");
                string name = ReadName();
                if (catalog.GeoEndpoints.ContainsKey(name)) throw Error("geo 方法重复: " + name);
                catalog.GeoEndpoints[name] = ParseEndpoint(name);
            }

            private void ParseToolTip(CatalogDefinition catalog)
            {
                ExpectIdentifier("tooltip");
                string group = ReadIdentifier();
                ExpectSymbol("=");
                string value = ReadString();
                ExpectSymbol(";");
                catalog.ToolTips[group] = value.Trim('\r', '\n');
            }

            private ProviderDefinition ParseProvider()
            {
                ExpectIdentifier("provider");
                var provider = new ProviderDefinition
                {
                    Group = ReadIdentifier(),
                    ID = ReadNumber()
                };
                ExpectSymbol("{");
                while (!IsSymbol("}"))
                {
                    if (IsIdentifier("ipv4"))
                    {
                        MoveNext();
                        provider.IPv4 = ParseEndpoint("IPv4");
                    }
                    else if (IsIdentifier("ipv6"))
                    {
                        MoveNext();
                        provider.IPv6 = ParseEndpoint("IPv6");
                    }
                    else if (IsIdentifier("use"))
                    {
                        MoveNext();
                        provider.GeoReference = ReadName();
                        ExpectSymbol(";");
                    }
                    else throw Error("Provider 中只允许 ipv4/ipv6/use");
                }
                ExpectSymbol("}");
                return provider;
            }

            private EndpointDefinition ParseEndpoint(string fallbackName)
            {
                string name = fallbackName;
                if (_current.Kind == TokenKind.String || _current.Kind == TokenKind.Identifier)
                    name = ReadName();
                var endpoint = new EndpointDefinition { Name = name };
                ExpectSymbol("{");
                while (!IsSymbol("}"))
                {
                    if (IsIdentifier("request")) endpoint.Statements.Add(ParseRequest());
                    else if (IsIdentifier("requestIf")) endpoint.Statements.Add(ParseConditionalRequest());
                    else if (IsIdentifier("let")) endpoint.Statements.Add(ParseLet());
                    else if (IsIdentifier("setTitleIf")) endpoint.Statements.Add(ParseSetTitle(true));
                    else if (IsIdentifier("setTitle")) endpoint.Statements.Add(ParseSetTitle(false));
                    else if (IsIdentifier("returnIf")) endpoint.Statements.Add(ParseConditionalReturn());
                    else if (IsIdentifier("return")) endpoint.Statements.Add(ParseReturn());
                    else if (IsIdentifier("returnGeo")) endpoint.Statements.Add(ParseReturnGeo(false));
                    else if (IsIdentifier("returnGeoIf")) endpoint.Statements.Add(ParseReturnGeo(true));
                    else throw Error("接口中只允许 request/requestIf/let/setTitle/setTitleIf/returnIf/return/returnGeo/returnGeoIf");
                }
                ExpectSymbol("}");
                return endpoint;
            }

            private RequestStatement ParseRequest()
            {
                ExpectIdentifier("request");
                var request = new RequestStatement { Variable = ReadIdentifier() };
                ParseRequestBody(request);
                return request;
            }

            private RequestStatement ParseConditionalRequest()
            {
                ExpectIdentifier("requestIf");
                Expression condition = ParseExpression();
                ExpectSymbol(",");
                var request = new RequestStatement
                {
                    Condition = condition,
                    Variable = ReadIdentifier()
                };
                ParseRequestBody(request);
                return request;
            }

            private void ParseRequestBody(RequestStatement request)
            {
                ExpectSymbol("{");
                while (!IsSymbol("}"))
                {
                    if (IsIdentifier("header"))
                    {
                        MoveNext();
                        string headerName = ReadString();
                        ExpectSymbol("=");
                        request.Headers[headerName] = ParseExpression();
                        ExpectSymbol(";");
                    }
                    else
                    {
                        string property = ReadIdentifier();
                        ExpectSymbol("=");
                        request.Properties[property] = ParseExpression();
                        ExpectSymbol(";");
                    }
                }
                ExpectSymbol("}");
            }

            private LetStatement ParseLet()
            {
                ExpectIdentifier("let");
                var statement = new LetStatement { Variable = ReadIdentifier() };
                ExpectSymbol("=");
                statement.Expression = ParseExpression();
                ExpectSymbol(";");
                return statement;
            }

            private SetTitleStatement ParseSetTitle(bool conditional)
            {
                ExpectIdentifier(conditional ? "setTitleIf" : "setTitle");
                var statement = new SetTitleStatement();
                if (conditional)
                {
                    statement.Condition = ParseExpression();
                    ExpectSymbol(",");
                }
                statement.Expression = ParseExpression();
                ExpectSymbol(";");
                return statement;
            }

            private ReturnStatement ParseReturn()
            {
                ExpectIdentifier("return");
                var statement = new ReturnStatement { Expression = ParseExpression() };
                ExpectSymbol(";");
                return statement;
            }

            private ConditionalReturnStatement ParseConditionalReturn()
            {
                ExpectIdentifier("returnIf");
                var statement = new ConditionalReturnStatement
                {
                    Condition = ParseExpression()
                };
                ExpectSymbol(",");
                statement.Expression = ParseExpression();
                ExpectSymbol(";");
                return statement;
            }

            private ReturnGeoStatement ParseReturnGeo(bool conditional)
            {
                ExpectIdentifier(conditional ? "returnGeoIf" : "returnGeo");
                var statement = new ReturnGeoStatement();
                if (conditional)
                {
                    statement.Condition = ParseExpression();
                    ExpectSymbol(",");
                }
                statement.Location = ParseExpression();
                ExpectSymbol(",");
                statement.AS = ParseExpression();
                ExpectSymbol(";");
                return statement;
            }

            private Expression ParseExpression()
            {
                if (_current.Kind == TokenKind.String)
                    return new LiteralExpression { Value = ReadString() };
                if (_current.Kind == TokenKind.Number)
                    return new LiteralExpression { Value = ReadNumber() };
                if (_current.Kind != TokenKind.Identifier)
                    throw Error("期待字符串、数字、变量或方法调用");

                string name = ReadIdentifier();
                if (string.Equals(name, "true", StringComparison.OrdinalIgnoreCase))
                    return new LiteralExpression { Value = true };
                if (string.Equals(name, "false", StringComparison.OrdinalIgnoreCase))
                    return new LiteralExpression { Value = false };
                if (string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
                    return new LiteralExpression { Value = null };

                if (!IsSymbol("(")) return new VariableExpression { Name = name };

                ExpectSymbol("(");
                var call = new CallExpression { Name = name };
                if (!IsSymbol(")"))
                {
                    while (true)
                    {
                        call.Arguments.Add(ParseExpression());
                        if (!IsSymbol(",")) break;
                        ExpectSymbol(",");
                    }
                }
                ExpectSymbol(")");
                return call;
            }

            private string ReadName()
            {
                return _current.Kind == TokenKind.String ? ReadString() : ReadIdentifier();
            }

            private string ReadIdentifier()
            {
                if (_current.Kind != TokenKind.Identifier) throw Error("期待标识符");
                string value = _current.Text;
                MoveNext();
                return value;
            }

            private string ReadString()
            {
                if (_current.Kind != TokenKind.String) throw Error("期待字符串");
                string value = _current.Text;
                MoveNext();
                return value;
            }

            private int ReadNumber()
            {
                if (_current.Kind != TokenKind.Number) throw Error("期待整数");
                int value = int.Parse(_current.Text, CultureInfo.InvariantCulture);
                MoveNext();
                return value;
            }

            private void ExpectIdentifier(string value)
            {
                if (!IsIdentifier(value)) throw Error("期待 " + value);
                MoveNext();
            }

            private void ExpectSymbol(string value)
            {
                if (!IsSymbol(value)) throw Error("期待符号 " + value);
                MoveNext();
            }

            private bool IsIdentifier(string value)
            {
                return _current.Kind == TokenKind.Identifier &&
                    string.Equals(_current.Text, value, StringComparison.OrdinalIgnoreCase);
            }

            private bool IsSymbol(string value)
            {
                return _current.Kind == TokenKind.Symbol && _current.Text == value;
            }

            private void MoveNext()
            {
                _current = _lexer.Next();
            }

            private FormatException Error(string message)
            {
                return new FormatException(message + "，第 " + _current.Line +
                    " 行，第 " + _current.Column + " 列，当前内容: " + _current.Text);
            }
        }
    }
}
