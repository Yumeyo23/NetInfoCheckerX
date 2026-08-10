using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetInfoCheckerX
{
    /// <summary>
    /// 通用文本解析工具集 —— 统一维护，所有窗口和程序集共用同一份。
    /// 包含：JSON路径取值 / 取中间文本 / 取左右 / 删左右 / IP正则提取 / Unicode解码 等。
    /// </summary>
    public static class TextHelper
    {
        #region JSON 路径解析

        /// <summary>
        /// 用 "." 分隔的路径从 JSON 字符串中提取值。
        /// 支持: key / key.sub / key[0] / key.sub[0].field 等写法。
        /// 兼容 ['key'] 和 ["key"] 格式。
        /// </summary>
        public static string ExtractJsonValue(string json, string path)
        {
            if (string.IsNullOrEmpty(json)) return "";

            string currentJson = json;
            // 兼容处理路径格式
            string normalizedPath = path.Replace("['", ".").Replace("']", "").Replace("[\"", ".").Replace("\"]", "");
            if (normalizedPath.StartsWith(".")) normalizedPath = normalizedPath.Substring(1);

            string[] steps = normalizedPath.Split('.');

            foreach (var step in steps)
            {
                string key = step;
                int arrayIndex = -1;

                // 提取数组下标，比如 path[0]
                if (step.Contains("[") && step.Contains("]"))
                {
                    var arrayMatch = Regex.Match(step, @"(?<name>.*?)\[(?<index>\d+)\]");
                    if (arrayMatch.Success)
                    {
                        key = arrayMatch.Groups["name"].Value;
                        arrayIndex = int.Parse(arrayMatch.Groups["index"].Value);
                    }
                }

                string searchKey = $"\"{key}\"";
                int keyIndex = currentJson.IndexOf(searchKey, StringComparison.Ordinal);
                if (keyIndex == -1) return "";

                int colonIndex = currentJson.IndexOf(':', keyIndex + searchKey.Length);
                if (colonIndex == -1) return "";

                int valueStartIndex = -1;
                for (int i = colonIndex + 1; i < currentJson.Length; i++)
                {
                    if (!char.IsWhiteSpace(currentJson[i])) { valueStartIndex = i; break; }
                }
                if (valueStartIndex == -1) return "";

                char firstChar = currentJson[valueStartIndex];
                string extractedValue = "";

                // 基础提取逻辑
                if (firstChar == '"')
                { // 字符串
                    int endQuoteIndex = currentJson.IndexOf('"', valueStartIndex + 1);
                    while (endQuoteIndex != -1 && currentJson[endQuoteIndex - 1] == '\\')
                        endQuoteIndex = currentJson.IndexOf('"', endQuoteIndex + 1);
                    if (endQuoteIndex != -1)
                        extractedValue = currentJson.Substring(valueStartIndex + 1, endQuoteIndex - valueStartIndex - 1);
                }
                else if (firstChar == '{' || firstChar == '[')
                { // 对象或数组
                    int balance = 0;
                    char open = firstChar;
                    char close = (firstChar == '{') ? '}' : ']';
                    for (int i = valueStartIndex; i < currentJson.Length; i++)
                    {
                        if (currentJson[i] == open) balance++;
                        else if (currentJson[i] == close) balance--;
                        if (balance == 0)
                        {
                            extractedValue = currentJson.Substring(valueStartIndex, i - valueStartIndex + 1);
                            break;
                        }
                    }
                }
                else
                { // 数字/布尔/null
                    int endIdx = currentJson.IndexOfAny(new char[] { ',', '}', ']' }, valueStartIndex);
                    if (endIdx == -1) endIdx = currentJson.Length;
                    extractedValue = currentJson.Substring(valueStartIndex, endIdx - valueStartIndex).Trim();
                    if (extractedValue.Equals("null", StringComparison.OrdinalIgnoreCase))
                        extractedValue = "";
                }

                // --- 处理数组 ---
                if (firstChar == '[')
                {
                    string content = extractedValue.Substring(1, extractedValue.Length - 2).Trim();
                    if (string.IsNullOrEmpty(content)) { currentJson = ""; continue; }

                    List<string> items = new List<string>();
                    int currentPos = 0;
                    int foundCount = 0;
                    bool matched = false;

                    while (currentPos < content.Length)
                    {
                        while (currentPos < content.Length && (char.IsWhiteSpace(content[currentPos]) || content[currentPos] == ','))
                            currentPos++;
                        if (currentPos >= content.Length) break;

                        int start = currentPos;
                        int itemEnd = -1;
                        // 判断这一项是对象、数组还是普通字符串
                        if (content[currentPos] == '{' || content[currentPos] == '[')
                        {
                            int b = 0;
                            char op = content[currentPos];
                            char cl = (op == '{' ? '}' : ']');
                            for (int j = currentPos; j < content.Length; j++)
                            {
                                if (content[j] == op) b++;
                                else if (content[j] == cl) b--;
                                if (b == 0) { itemEnd = j; break; }
                            }
                        }
                        else if (content[currentPos] == '"')
                        {
                            int eq = content.IndexOf('"', currentPos + 1);
                            while (eq != -1 && content[eq - 1] == '\\')
                                eq = content.IndexOf('"', eq + 1);
                            itemEnd = eq;
                        }
                        else
                        {
                            itemEnd = content.IndexOfAny(new char[] { ',', ']' }, currentPos);
                            if (itemEnd == -1) itemEnd = content.Length - 1;
                            else itemEnd--;
                        }

                        string targetItem = content.Substring(start, itemEnd - start + 1).Trim().Trim('"');

                        if (arrayIndex == -1)
                        { // 没有指定下标，把所有项加进列表
                            items.Add(targetItem);
                        }
                        else if (foundCount == arrayIndex)
                        { // 指定了下标，只取那一个
                            currentJson = targetItem;
                            matched = true;
                            break;
                        }
                        foundCount++;
                        currentPos = itemEnd + 1;
                    }

                    if (arrayIndex == -1)
                        currentJson = string.Join("/", items); // 合并所有结果
                    else if (!matched)
                        return "";
                }
                else
                {
                    currentJson = extractedValue;
                }
            }
            return currentJson;
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
            if (content == "请求超时。" ||
                content == "操作已被用户取消。" ||
                content == "发送请求时出错。" ||
                content.Contains("网络连接失败") ||
                content.Contains("网络打不开捏"))
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
            if (string.IsNullOrEmpty(json) || keys == null || keys.Length == 0) return "";
            string currentJson = json;
            for (int i = 0; i < keys.Length; i++)
            {
                string searchKey = $"\"{keys[i]}\"";
                int keyIndex = currentJson.IndexOf(searchKey, StringComparison.Ordinal);
                if (keyIndex == -1) return "";
                int colonIndex = currentJson.IndexOf(':', keyIndex + searchKey.Length);
                if (colonIndex == -1) return "";
                int valueStartIndex = -1;
                for (int j = colonIndex + 1; j < currentJson.Length; j++)
                {
                    if (!char.IsWhiteSpace(currentJson[j])) { valueStartIndex = j; break; }
                }
                if (valueStartIndex == -1) return "";
                char firstChar = currentJson[valueStartIndex];
                if (firstChar == '"')
                {
                    int endQuoteIndex = currentJson.IndexOf('"', valueStartIndex + 1);
                    if (endQuoteIndex == -1) return "";
                    string strVal = currentJson.Substring(valueStartIndex + 1, endQuoteIndex - valueStartIndex - 1);
                    if (i == keys.Length - 1) return strVal;
                    currentJson = strVal;
                }
                else if (firstChar == '{' || firstChar == '[')
                {
                    int depth = 1;
                    int endPos = valueStartIndex + 1;
                    bool inString = false;
                    while (endPos < currentJson.Length && depth > 0)
                    {
                        char c = currentJson[endPos];
                        if (c == '"' && (endPos == valueStartIndex + 1 || currentJson[endPos - 1] != '\\')) inString = !inString;
                        if (!inString)
                        {
                            if (c == '{' || c == '[') depth++;
                            else if (c == '}' || c == ']') depth--;
                        }
                        endPos++;
                    }
                    if (depth != 0) return "";
                    string objVal = currentJson.Substring(valueStartIndex, endPos - valueStartIndex);
                    if (i == keys.Length - 1) return objVal;
                    currentJson = objVal;
                }
                else
                {
                    int endPos = valueStartIndex;
                    while (endPos < currentJson.Length && (char.IsDigit(currentJson[endPos]) || currentJson[endPos] == '.' || currentJson[endPos] == '-'))
                        endPos++;
                    string numVal = currentJson.Substring(valueStartIndex, endPos - valueStartIndex);
                    if (i == keys.Length - 1) return numVal;
                    currentJson = numVal;
                }
            }
            return currentJson;
        }

        #endregion
    }
}
