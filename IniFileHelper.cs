using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NetInfoCheckerX
{
    /// <summary>
    /// Reads and writes INI files as ordinary UTF-8 text. Existing UTF-16 and
    /// system-ANSI files are decoded and migrated to UTF-8 with a BOM on first use.
    /// </summary>
    internal static class IniFileHelper
    {
        private static readonly object SyncRoot = new object();
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static int GetPrivateProfileString(
            string section,
            string key,
            string defaultValue,
            StringBuilder buffer,
            int size,
            string filePath)
        {
            string value = defaultValue ?? string.Empty;

            try
            {
                lock (SyncRoot)
                {
                    bool needsMigration;
                    string text = ReadText(filePath, out needsMigration);
                    value = IniDocument.Parse(text).GetValue(section, key, value);

                    // A read also repairs an already-unreadable legacy file. Failure to
                    // migrate must not prevent the program from using the value it read.
                    if (needsMigration && File.Exists(filePath))
                    {
                        try
                        {
                            File.WriteAllText(filePath, text, Utf8WithBom);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
                value = defaultValue ?? string.Empty;
            }

            if (buffer == null || size <= 0)
                return 0;

            int maxLength = Math.Max(0, size - 1);
            if (value.Length > maxLength)
                value = value.Substring(0, maxLength);

            buffer.Clear();
            buffer.Append(value);
            return value.Length;
        }

        public static int WritePrivateProfileString(
            string section,
            string key,
            string value,
            string filePath)
        {
            try
            {
                lock (SyncRoot)
                {
                    bool ignored;
                    IniDocument document = IniDocument.Parse(ReadText(filePath, out ignored));

                    if (key == null)
                        document.DeleteSection(section);
                    else if (value == null)
                        document.DeleteValue(section, key);
                    else
                        document.SetValue(section, key, SanitizeValue(value));

                    string directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    File.WriteAllText(filePath, document.ToString(), Utf8WithBom);
                }

                return 1;
            }
            catch
            {
                // Match the old Win32 calls: callers can treat zero as a failed write.
                return 0;
            }
        }

        private static string ReadText(string filePath, out bool needsMigration)
        {
            needsMigration = false;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return string.Empty;

            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length == 0)
            {
                needsMigration = true;
                return string.Empty;
            }

            if (HasPrefix(bytes, 0xEF, 0xBB, 0xBF))
                return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);

            needsMigration = true;

            if (HasPrefix(bytes, 0xFF, 0xFE))
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            if (HasPrefix(bytes, 0xFE, 0xFF))
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            if (LooksLikeUtf16(bytes, littleEndian: true))
                return Encoding.Unicode.GetString(bytes);

            if (LooksLikeUtf16(bytes, littleEndian: false))
                return Encoding.BigEndianUnicode.GetString(bytes);

            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes);
            }
        }

        private static bool HasPrefix(byte[] bytes, params byte[] prefix)
        {
            if (bytes.Length < prefix.Length)
                return false;

            for (int i = 0; i < prefix.Length; i++)
            {
                if (bytes[i] != prefix[i])
                    return false;
            }

            return true;
        }

        private static bool LooksLikeUtf16(byte[] bytes, bool littleEndian)
        {
            int pairs = Math.Min(bytes.Length / 2, 256);
            if (pairs < 2)
                return false;

            int expectedZeroBytes = 0;
            int otherZeroBytes = 0;
            for (int i = 0; i < pairs; i++)
            {
                byte first = bytes[i * 2];
                byte second = bytes[i * 2 + 1];
                byte expected = littleEndian ? second : first;
                byte other = littleEndian ? first : second;

                if (expected == 0) expectedZeroBytes++;
                if (other == 0) otherZeroBytes++;
            }

            return expectedZeroBytes >= Math.Max(2, pairs / 3)
                && expectedZeroBytes > otherZeroBytes * 2;
        }

        private static string SanitizeValue(string value)
        {
            return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        private sealed class IniDocument
        {
            private readonly List<string> _lines;

            private IniDocument(List<string> lines)
            {
                _lines = lines;
            }

            public static IniDocument Parse(string text)
            {
                if (string.IsNullOrEmpty(text))
                    return new IniDocument(new List<string>());

                string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
                return new IniDocument(new List<string>(normalized.Split('\n')));
            }

            public string GetValue(string section, string key, string defaultValue)
            {
                if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
                    return defaultValue;

                int start;
                int end;
                if (!FindSection(section, out start, out end))
                    return defaultValue;

                for (int i = start + 1; i < end; i++)
                {
                    string foundKey;
                    string foundValue;
                    if (TryParseKeyValue(_lines[i], out foundKey, out foundValue)
                        && string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return foundValue;
                    }
                }

                return defaultValue;
            }

            public void SetValue(string section, string key, string value)
            {
                if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
                    return;

                int start;
                int end;
                if (FindSection(section, out start, out end))
                {
                    for (int i = start + 1; i < end; i++)
                    {
                        string foundKey;
                        string ignored;
                        if (TryParseKeyValue(_lines[i], out foundKey, out ignored)
                            && string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
                        {
                            int equalsIndex = _lines[i].IndexOf('=');
                            _lines[i] = _lines[i].Substring(0, equalsIndex + 1) + value;
                            return;
                        }
                    }

                    int insertIndex = end;
                    while (insertIndex > start + 1 && string.IsNullOrWhiteSpace(_lines[insertIndex - 1]))
                        insertIndex--;
                    _lines.Insert(insertIndex, key + "=" + value);
                    return;
                }

                if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[_lines.Count - 1]))
                    _lines.Add(string.Empty);

                _lines.Add("[" + section + "]");
                _lines.Add(key + "=" + value);
            }

            public void DeleteValue(string section, string key)
            {
                if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
                    return;

                int start;
                int end;
                if (!FindSection(section, out start, out end))
                    return;

                for (int i = end - 1; i > start; i--)
                {
                    string foundKey;
                    string ignored;
                    if (TryParseKeyValue(_lines[i], out foundKey, out ignored)
                        && string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        _lines.RemoveAt(i);
                    }
                }
            }

            public void DeleteSection(string section)
            {
                if (string.IsNullOrEmpty(section))
                    return;

                int start;
                int end;
                if (FindSection(section, out start, out end))
                    _lines.RemoveRange(start, end - start);
            }

            public override string ToString()
            {
                return string.Join(Environment.NewLine, _lines);
            }

            private bool FindSection(string section, out int start, out int end)
            {
                start = -1;
                end = _lines.Count;

                for (int i = 0; i < _lines.Count; i++)
                {
                    string foundSection;
                    if (!TryParseSection(_lines[i], out foundSection))
                        continue;

                    if (start >= 0)
                    {
                        end = i;
                        return true;
                    }

                    if (string.Equals(foundSection, section, StringComparison.OrdinalIgnoreCase))
                        start = i;
                }

                return start >= 0;
            }

            private static bool TryParseSection(string line, out string section)
            {
                section = null;
                if (line == null)
                    return false;

                string trimmed = line.Trim();
                if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[trimmed.Length - 1] != ']')
                    return false;

                section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                return section.Length > 0;
            }

            private static bool TryParseKeyValue(string line, out string key, out string value)
            {
                key = null;
                value = null;
                if (string.IsNullOrWhiteSpace(line))
                    return false;

                string trimmedStart = line.TrimStart();
                if (trimmedStart.StartsWith(";", StringComparison.Ordinal)
                    || trimmedStart.StartsWith("#", StringComparison.Ordinal))
                {
                    return false;
                }

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex < 0)
                    return false;

                key = line.Substring(0, equalsIndex).Trim();
                value = line.Substring(equalsIndex + 1).Trim();
                return key.Length > 0;
            }
        }
    }
}
