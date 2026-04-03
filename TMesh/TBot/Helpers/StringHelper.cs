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

        private static readonly char[] EscapeCharsV2 =
    [
        '_', '*', '[', ']', '(', ')', '~', '`', '>', '#',
        '+', '-', '=', '|', '{', '}', '.', '!'
    ];

        private static readonly char[] EscapeCharsV1 =
   [
       '_', '*', '[', '`'
   ];

        public static string EscapeMd(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length * 2);

            foreach (char c in text)
            {
                if (EscapeCharsV1.Contains(c))
                    sb.Append('\\');

                sb.Append(c);
            }

            return sb.ToString();
        }


        public static string EscapeMdV2(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length * 2);

            foreach (char c in text)
            {
                if (EscapeCharsV2.Contains(c))
                    sb.Append('\\');

                sb.Append(c);
            }

            return sb.ToString();
        }


    }
}
