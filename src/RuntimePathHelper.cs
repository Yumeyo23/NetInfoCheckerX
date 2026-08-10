using System;
using System.IO;

namespace NetInfoCheckerX
{
    internal static class RuntimePathHelper
    {
        internal static bool IsTemporaryRun()
        {
            return IsTemporaryRun(
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetTempPath());
        }

        internal static bool IsTemporaryRun(string currentPath, string tempPath)
        {
            if (string.IsNullOrWhiteSpace(currentPath) ||
                string.IsNullOrWhiteSpace(tempPath))
            {
                return false;
            }

            bool isTempDirectory = currentPath.StartsWith(
                tempPath,
                StringComparison.OrdinalIgnoreCase);

            string[] sfxKeywords = { "Rar", "7z", "HZ$", "Sfx", "Temp", "Tmp" };
            foreach (string keyword in sfxKeywords)
            {
                if (currentPath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return isTempDirectory;
        }
    }
}
