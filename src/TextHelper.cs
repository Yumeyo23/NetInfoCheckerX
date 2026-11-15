using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NetInfoCheckerX
{
    /// <summary>
    /// 通用文本解析工具集 —— 统一维护，所有窗口和程序集共用同一份。
    /// 包含：JSON路径取值 / 取中间文本 / 取左右 / 删左右 / IP正则提取 / Unicode解码 等。
    /// </summary>
    public static class TextHelper
    {
        /// <summary>
        /// Removes URL wrappers and characters that are unsafe or irrelevant in a
        /// network target, while retaining Unicode letters and digits for IDN hosts.
        /// </summary>
        public static string SanitizeTargetInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            string value = input.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(7);
            else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(8);

            var result = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length;)
            {
                char current = value[index];
                if (current == '\u3002' || current == '\uFF0E' || current == '\uFF61')
                {
                    result.Append('.');
                    index++;
                    continue;
                }

                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(value, index);
                int charCount = char.IsSurrogatePair(value, index) ? 2 : 1;
                bool isLetterOrNumber = category == UnicodeCategory.UppercaseLetter ||
                    category == UnicodeCategory.LowercaseLetter ||
                    category == UnicodeCategory.TitlecaseLetter ||
                    category == UnicodeCategory.ModifierLetter ||
                    category == UnicodeCategory.OtherLetter ||
                    category == UnicodeCategory.DecimalDigitNumber;
                bool isCombiningMark = category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark;

                if (isLetterOrNumber || isCombiningMark || current == '.' || current == ':' ||
                    current == '-' || current == '_')
                    result.Append(value, index, charCount);

                index += charCount;
            }

            return result.ToString();
        }

        #region JSON 路径解析

        /// <summary>
        /// 用 "." 分隔的路径从 JSON 字符串中提取值。
        /// 支持: key / key.sub / key[0] / key.sub[0].field 等写法。
        /// 兼容 ['key'] 和 ["key"] 格式。
        /// </summary>
        public static string ExtractJsonValue(string json, string path)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(path)) return "";
            try
            {
                JsonNode current = new JsonReader(json).Read();
                int position = 0;
                bool firstStep = true;
                while (position < path.Length)
                {
                    if (path[position] == '.')
                    {
                        position++;
                        if (position == path.Length) return "";
                    }
                    else if (!firstStep && path[position] != '[') return "";
                    firstStep = false;

                    if (path[position] == '[')
                    {
                        position++;
                        if (position >= path.Length) return "";
                        char quote = path[position];
                        if (quote == '\'' || quote == '"')
                        {
                            position++;
                            var key = new StringBuilder();
                            bool closed = false;
                            while (position < path.Length)
                            {
                                char c = path[position++];
                                if (c == quote) { closed = true; break; }
                                if (c == '\\' && position < path.Length)
                                    c = path[position++];
                                key.Append(c);
                            }
                            if (!closed || position >= path.Length || path[position++] != ']') return "";
                            current = GetJsonChild(current, key.ToString());
                        }
                        else
                        {
                            int start = position;
                            while (position < path.Length && char.IsDigit(path[position])) position++;
                            int index;
                            if (position == start || position >= path.Length || path[position++] != ']' ||
                                !int.TryParse(path.Substring(start, position - start - 1), out index) ||
                                current.Items == null || index >= current.Items.Count) return "";
                            current = current.Items[index];
                        }
                    }
                    else
                    {
                        int start = position;
                        while (position < path.Length && path[position] != '.' && path[position] != '[') position++;
                        current = GetJsonChild(current, path.Substring(start, position - start));
                    }
                    if (current == null) return "";
                }
                return JsonText(json, current, true);
            }
            catch (FormatException) { return ""; }
            catch (ArgumentException) { return ""; }
        }

        private static JsonNode GetJsonChild(JsonNode node, string key)
        {
            JsonNode child;
            return node != null && node.Properties != null && node.Properties.TryGetValue(key, out child)
                ? child : null;
        }

        private static string JsonText(string source, JsonNode node, bool joinArray)
        {
            if (node.Kind == '"') return source.Substring(node.Start + 1, node.Length - 2);
            if (node.Kind == 'n') return "";
            if (joinArray && node.Items != null)
            {
                var items = new List<string>();
                foreach (JsonNode item in node.Items)
                {
                    // Preserve the historic slash-joined array format, including raw nested containers/null.
                    items.Add(item.Kind == '"' ? source.Substring(item.Start + 1, item.Length - 2)
                        : source.Substring(item.Start, item.Length));
                }
                return string.Join("/", items);
            }
            return source.Substring(node.Start, node.Length);
        }

        private sealed class JsonNode
        {
            internal int Start;
            internal int Length;
            internal char Kind;
            internal Dictionary<string, JsonNode> Properties;
            internal List<JsonNode> Items;
        }

        // Parse structure once; retain spans into the original text so formatting and numeric/string
        // representations do not change as a side effect of fixing path lookup.
        private sealed class JsonReader
        {
            private static readonly Regex Number = new Regex(
                @"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?",
                RegexOptions.CultureInvariant);
            private readonly string source;
            private readonly System.Web.Script.Serialization.JavaScriptSerializer strings =
                new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            private int position;

            internal JsonReader(string source) { this.source = source; }

            internal JsonNode Read()
            {
                if (source.Length > 0 && source[0] == '\ufeff') position++;
                JsonNode node = ReadValue(0);
                WhiteSpace();
                if (position != source.Length) throw new FormatException("JSON 后存在多余内容");
                return node;
            }

            private void WhiteSpace()
            {
                while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
            }

            private void Expect(char c)
            {
                WhiteSpace();
                if (position >= source.Length || source[position++] != c) throw new FormatException("JSON 格式错误");
            }

            private JsonNode ReadValue(int depth)
            {
                if (depth > 128) throw new FormatException("JSON 嵌套过深");
                WhiteSpace();
                if (position >= source.Length) throw new FormatException("JSON 未结束");
                var node = new JsonNode { Start = position, Kind = source[position] };
                switch (node.Kind)
                {
                    case '{':
                        position++;
                        node.Properties = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
                        WhiteSpace();
                        if (position < source.Length && source[position] != '}')
                        {
                            while (true)
                            {
                                WhiteSpace();
                                int start = position;
                                ReadString();
                                string key = strings.Deserialize<string>(source.Substring(start, position - start));
                                Expect(':');
                                JsonNode child = ReadValue(depth + 1);
                                // Keep first-match behavior for duplicate keys in the same object.
                                if (!node.Properties.ContainsKey(key)) node.Properties.Add(key, child);
                                WhiteSpace();
                                if (position >= source.Length || source[position] != ',') break;
                                position++;
                            }
                        }
                        Expect('}');
                        break;
                    case '[':
                        position++;
                        node.Items = new List<JsonNode>();
                        WhiteSpace();
                        if (position < source.Length && source[position] != ']')
                        {
                            while (true)
                            {
                                node.Items.Add(ReadValue(depth + 1));
                                WhiteSpace();
                                if (position >= source.Length || source[position] != ',') break;
                                position++;
                            }
                        }
                        Expect(']');
                        break;
                    case '"':
                        ReadString();
                        break;
                    case 't': Literal("true"); break;
                    case 'f': Literal("false"); break;
                    case 'n': Literal("null"); break;
                    default:
                        Match match = Number.Match(source, position);
                        if (!match.Success) throw new FormatException("JSON 值无效");
                        position += match.Length;
                        break;
                }
                node.Length = position - node.Start;
                return node;
            }

            private void Literal(string literal)
            {
                if (position + literal.Length > source.Length ||
                    string.CompareOrdinal(source, position, literal, 0, literal.Length) != 0)
                    throw new FormatException("JSON 值无效");
                position += literal.Length;
            }

            private void ReadString()
            {
                Expect('"');
                while (position < source.Length)
                {
                    char c = source[position++];
                    if (c == '"') return;
                    if (c < ' ') throw new FormatException("JSON 字符串包含控制字符");
                    if (c != '\\') continue;
                    if (position >= source.Length) break;
                    char escape = source[position++];
                    if (escape == 'u')
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            if (position >= source.Length || !Uri.IsHexDigit(source[position++]))
                                throw new FormatException("JSON Unicode 转义无效");
                        }
                    }
                    else if ("\"\\/bfnrt".IndexOf(escape) < 0) throw new FormatException("JSON 转义无效");
                }
                throw new FormatException("JSON 字符串未结束");
            }
        }

        #endregion

        #region 文本_取出中间文本

        /// <summary>
        /// 文本_取出中间文本 (模仿精易模块)
        /// </summary>
        /// <param name="fullText">原文本</param>
        /// <param name="leftText">左边文本</param>
        /// <param name="rightText">右边文本</param>
        /// <returns>中间的文本，找不到则返回空字符串</returns>
        public static string GetMidText(string fullText, string leftText, string rightText)
        {
            if (string.IsNullOrEmpty(fullText) || string.IsNullOrEmpty(leftText) || string.IsNullOrEmpty(rightText))
                return "";

            try
            {
                int leftIndex = fullText.IndexOf(leftText, StringComparison.Ordinal);
                if (leftIndex == -1) return "";

                int startIndex = leftIndex + leftText.Length;
                int rightIndex = fullText.IndexOf(rightText, startIndex, StringComparison.Ordinal);
                if (rightIndex == -1) return "";

                return fullText.Substring(startIndex, rightIndex - startIndex);
            }
            catch
            {
                return "";
            }
        }

        #endregion

        #region 文本_替换

        /// <summary>
        /// 文本_替换 (简单包装)
        /// </summary>
        public static string ReplaceText(string fullText, string oldText, string newText)
        {
            if (fullText == null) return "";
            return fullText.Replace(oldText, newText);
        }

        #endregion

        #region 文本_取左边 / 文本_取右边

        /// <summary>
        /// 文本_取左边
        /// </summary>
        public static string GetLeftText(string fullText, string countOrText)
        {
            if (string.IsNullOrEmpty(fullText)) return "";
            int index = fullText.IndexOf(countOrText, StringComparison.Ordinal);
            if (index == -1) return "";
            return fullText.Substring(0, index);
        }

        /// <summary>
        /// 文本_取右边
        /// </summary>
        public static string GetRightText(string fullText, string countOrText)
        {
            if (string.IsNullOrEmpty(fullText)) return "";
            int index = fullText.LastIndexOf(countOrText, StringComparison.Ordinal);
            if (index == -1) return "";
            return fullText.Substring(index + countOrText.Length);
        }

        #endregion

        #region 文本_删左边 / 文本_删右边

        /// <summary>
        /// 文本_删左边
        /// </summary>
        public static string StrDeleteLeft(string source, int length)
        {
            if (string.IsNullOrEmpty(source) || length <= 0)
                return source;

            if (length >= source.Length)
                return "";

            return source.Substring(length);
        }

        /// <summary>
        /// 文本_删右边
        /// </summary>
        public static string StrDeleteRight(string source, int length)
        {
            if (string.IsNullOrEmpty(source) || length <= 0)
                return source;

            if (length >= source.Length)
                return "";

            return source.Substring(0, source.Length - length);
        }

        #endregion

        #region IP 提取与验证

        /// <summary>
        /// 万能提取方法：优先识别 HttpHelper 的报错，其次正则提取 IP
        /// </summary>
        public static string UniversalExtractIP(string content, bool isIPv6)
        {
            // 1. 基础空值检查
            if (string.IsNullOrWhiteSpace(content))
            {
                return "返回空。";
            }

            // 2. 优先识别 HttpHelper 抛出的"硬核"网络错误
            if (IsHttpHelperError(content))
            {
                return content;
            }

            // 3. 正则提取
            const string patternV4 = @"\b(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b";
            const string patternV6 = @"(?i)\b(?:(?:[a-f0-9]{1,4}:){7}[a-f0-9]{1,4}|(?:[a-f0-9]{1,4}:){1,7}:|:(?::[a-f0-9]{1,4}){1,7}|(?:[a-f0-9]{1,4}:){1,6}:[a-f0-9]{1,4}|(?:[a-f0-9]{1,4}:){1,5}(?::[a-f0-9]{1,4}){1,2}|(?:[a-f0-9]{1,4}:){1,4}(?::[a-f0-9]{1,4}){1,3}|(?:[a-f0-9]{1,4}:){1,3}(?::[a-f0-9]{1,4}){1,4}|(?:[a-f0-9]{1,4}:){1,2}(?::[a-f0-9]{1,4}){1,5}|[a-f0-9]{1,4}:(?::[a-f0-9]{1,4}){1,6}|::)\b";

            string pattern = isIPv6 ? patternV6 : patternV4;
            var match = Regex.Match(content, pattern);

            if (match.Success)
            {
                string ip = match.Value.Trim().Replace("\"", "");
                if (isIPv6 || IsValidIPv4(ip))
                {
                    return ip;
                }
            }

            // 4. 检查常见的 HTTP 状态码或 API 级错误
            if (Regex.IsMatch(content, @"\b(5\d{2}|4\d{2})\b") ||
                content.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string errorMsg = content.Length > 50 ? content.Substring(0, 50) + "..." : content;
                return $"{errorMsg}";
            }

            // 5. 最后一道防线
            Console.WriteLine($">>[正则匹配方法] 未匹配到IP地址，输出欲匹配原文:\n{content}");
            return "未返回有效IP。";
        }

        /// <summary>
        /// 从双栈接口返回内容中按出现顺序提取并验证第一个 IPv4 或 IPv6 地址。
        /// </summary>
        public static string UniversalValidateIP(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "返回类型不是IP地址。";
            }

            // HttpHelper 的错误文本里可能包含目标服务器 IP，必须先于 IP 提取处理。
            if (IsHttpHelperError(content))
            {
                return content;
            }

            const string patternV4 = @"(?<![0-9]\.)\b(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b(?!\.[0-9])";
            const string patternV6Candidate = @"(?<![0-9a-fA-F:.])(?=[0-9a-fA-F:.]*:)[0-9a-fA-F:.]+(?![0-9a-fA-F:.])";

            var matches = new List<Match>();
            foreach (Match match in Regex.Matches(content, patternV4))
                matches.Add(match);
            foreach (Match match in Regex.Matches(content, patternV6Candidate))
                matches.Add(match);
            matches.Sort((left, right) => left.Index.CompareTo(right.Index));

            foreach (Match match in matches)
            {
                IPAddress address;
                if (IPAddress.TryParse(match.Value, out address) &&
                    (address.AddressFamily == AddressFamily.InterNetwork ||
                     address.AddressFamily == AddressFamily.InterNetworkV6))
                {
                    return address.ToString();
                }
            }

            return "返回类型不是IP地址。";
        }

        /// <summary>
        /// 识别 HttpHelper 各请求入口可能返回的固定错误文本及常见网络异常。
        /// </summary>
        private static bool IsHttpHelperError(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;

            string normalized = content.Trim().TrimEnd('。');
            switch (normalized)
            {
                case "请求超时":
                case "DNS解析超时":
                case "操作已被用户取消":
                case "操作被取消":
                case "发送请求时出错":
                case "未找到IPv4地址":
                case "未找到IPv6地址":
                case "指定的本地 IP 与强制使用的地址族不一致":
                case "不能同时指定 forceIPv4 和 forceIPv6":
                case "未找到查询器X内置curl":
                case "返回空":
                    return true;
            }

            string[] errorFragments =
            {
                "网络连接失败",
                "网络打不开捏",
                "远程名无法解析",
                "未能解析此远程名称",
                "无法连接到远程服务器",
                "基础连接已经关闭",
                "连接尝试失败",
                "目标计算机积极拒绝",
                "请求被中止",
                "An error occurred while sending the request",
                "The remote name could not be resolved",
                "No such host is known",
                "Unable to connect to the remote server",
                "A connection attempt failed",
                "actively refused it",
                "The request was aborted"
            };

            foreach (string fragment in errorFragments)
            {
                if (content.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// IPv4 额外验证方法
        /// </summary>
        public static bool IsValidIPv4(string ip)
        {
            try
            {
                var parts = ip.Split('.');
                if (parts.Length != 4) return false;

                foreach (var part in parts)
                {
                    int num = int.Parse(part);
                    if (num < 0 || num > 255) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// IPv6 额外验证方法。必须含冒号，避免 IPAddress.TryParse 把 "1"
        /// 一类的纯数字串当成 0.0.0.1。大小写不敏感，不需要写 []。
        /// </summary>
        public static bool IsValidIPv6(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;

            string candidate = ip.Trim();
            if (candidate.IndexOf(':') < 0) return false;

            try
            {
                return IPAddress.TryParse(candidate, out IPAddress address) &&
                    address.AddressFamily == AddressFamily.InterNetworkV6;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 同时接受 IPv4 与 IPv6 的验证方法，返回规范化后的地址文本。
        /// </summary>
        public static bool TryNormalizeIP(string ip, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(ip)) return false;

            string candidate = ip.Trim();
            if (!IsValidIPv4(candidate) && !IsValidIPv6(candidate)) return false;

            normalized = IPAddress.TryParse(candidate, out IPAddress address)
                ? address.ToString() : candidate;
            return true;
        }

        #endregion

        #region Unicode 解码

        /// <summary>
        /// 解码 .NET 转义序列，例如 \uXXXX。
        /// </summary>
        public static string UnescapeUnicode(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            return Regex.Unescape(str);
        }

        /// <summary>
        /// 按层级 key 序列导航 JSON（避免 key 含点号时 ExtractJsonValue 误分割的问题）
        /// </summary>
        public static string ExtractJsonValueByKeys(string json, params string[] keys)
        {
            if (string.IsNullOrWhiteSpace(json) || keys == null || keys.Length == 0) return "";
            try
            {
                JsonNode current = new JsonReader(json).Read();
                foreach (string key in keys)
                {
                    if (key == null) return "";
                    current = GetJsonChild(current, key);
                    if (current == null) return "";
                }
                return JsonText(json, current, false);
            }
            catch (FormatException) { return ""; }
            catch (ArgumentException) { return ""; }
        }

        #endregion
    }
}
