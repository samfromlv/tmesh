using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Helpers
{
    public class StringHelper
    {
        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        /// <summary>
        /// Escapes special characters for Telegram Markdown v1 in user-supplied strings.
        /// Handles: _ * ` [
        /// </summary>
        public static string EscapeMd(string text)
        {
            return text?.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[") ?? string.Empty;
        }
    }
}
