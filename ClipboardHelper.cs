using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    /// <summary>
    /// 剪贴板可能被其他进程短暂锁定，统一进行有限次数重试，避免 UI 事件直接崩溃。
    /// </summary>
    internal static class ClipboardHelper
    {
        private const int MaxAttempts = 5;

        public static bool TryGetText(out string text)
        {
            string result = string.Empty;
            bool succeeded = TryExecute(() => result = Clipboard.GetText());
            text = succeeded ? result : string.Empty;
            return succeeded;
        }

        public static bool TrySetText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return TryExecute(() => Clipboard.SetText(text));
        }

        /// <summary>
        /// 为结果控件绑定双击复制，并以短暂加粗作为复制成功反馈。
        /// </summary>
        public static void BindDoubleClickCopy(Control control)
        {
            if (control == null) return;

            Font originalFont = null;
            Font flashFont = null;
            int flashVersion = 0;

            control.MouseDoubleClick += async (sender, e) =>
            {
                if (e.Button != MouseButtons.Left || string.IsNullOrEmpty(control.Text)) return;
                if (!TrySetText(control.Text)) return;

                int currentVersion = ++flashVersion;
                if (originalFont == null) originalFont = control.Font;

                var nextFlashFont = new Font(originalFont, originalFont.Style | FontStyle.Bold);
                var previousFlashFont = flashFont;
                flashFont = nextFlashFont;
                control.Font = nextFlashFont;
                previousFlashFont?.Dispose();

                await Task.Delay(100);

                // 连续双击时，仅由最后一次事件负责恢复，避免提前结束闪烁。
                if (currentVersion != flashVersion) return;

                var fontToDispose = flashFont;
                flashFont = null;
                if (!control.IsDisposed && originalFont != null)
                {
                    control.Font = originalFont;
                }
                originalFont = null;
                fontToDispose?.Dispose();
            };
        }

        public static bool TrySetImage(Image image)
        {
            if (image == null) return false;
            return TryExecute(() => Clipboard.SetImage(image));
        }

        public static bool TryClear()
        {
            return TryExecute(Clipboard.Clear);
        }

        private static bool TryExecute(Action action)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    action();
                    return true;
                }
                catch (ExternalException)
                {
                    if (attempt == MaxAttempts - 1) return false;
                    Thread.Sleep(20 * (attempt + 1));
                }
                catch (ThreadStateException)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
