using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Helpers
{
    public static class MqttPasswordDerive
    {
        /// <summary>
        /// Derive password as: first 23 chars of base64url_no_padding(SHA256(username + secret)).
        /// Matches the plugin logic exactly.
        /// </summary>
        public static string DerivePassword(string username, string secret)
        {
            if (username is null) throw new ArgumentNullException(nameof(username));
            if (secret is null) throw new ArgumentNullException(nameof(secret));

            // plugin concatenates raw bytes of username + secret (effectively UTF-8 in our .NET implementation)
            byte[] input = Encoding.UTF8.GetBytes(username + secret);

            byte[] digest = SHA256.HashData(input); // .NET 8

            // Base64url (RFC 4648) without padding, using '-' and '_' and no '='
            string b64 = Convert.ToBase64String(digest)
                .TrimEnd('=')        // no padding
                .Replace('+', '-')   // url-safe
                .Replace('/', '_');  // url-safe

            // SHA256 => 32 bytes => base64url_no_pad length is always 43 chars, so 23 is safe
            return b64.Length >= 23 ? b64.Substring(0, 23) : b64;
        }

        /// <summary>
        /// Validate that a provided password matches the derived one (constant-time compare).
        /// </summary>
        public static bool Validate(string username, string secret, string providedPassword)
        {
            if (providedPassword is null) return false;
            if (providedPassword.Length != 23) return false;

            string expected = DerivePassword(username, secret);

            // constant-time comparison for equal-length strings
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(providedPassword)
            );
        }
    }

}
