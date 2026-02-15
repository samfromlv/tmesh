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
        /// Derive password as: Last two characters of username uppercase than _ than first 23 chars of base64url_no_padding(SHA256(username + secret)).
        /// Actual MQTT password is without first 3 characters
        /// TMesh firmare removed first 3 characters when sendign mqtt password
        /// </summary>
        public static string DerivePassword(string username, string secret)
        {
            ArgumentNullException.ThrowIfNull(username);
            ArgumentNullException.ThrowIfNull(secret);

            // plugin concatenates raw bytes of username + secret (effectively UTF-8 in our .NET implementation)
            byte[] input = Encoding.UTF8.GetBytes(username + secret);

            byte[] digest = SHA256.HashData(input); // .NET 8

            // Base64url (RFC 4648) without padding, using '-' and '_' and no '='
            string b64 = Convert.ToBase64String(digest)
                .TrimEnd('=')        // no padding
                .Replace('+', '-')   // url-safe
                .Replace('/', '_');  // url-safe

            // SHA256 => 32 bytes => base64url_no_pad length is always 43 chars, so 23 is safe
            var pwd = b64.Length >= 23 ? b64[..23] : b64;

            var prefix = username[^2..];
            return $"{prefix}_{pwd}";
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
