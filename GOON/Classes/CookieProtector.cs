using System;
using System.Security.Cryptography;
using System.Text;

namespace GOON.Classes {
    /// <summary>
    /// Encrypts/decrypts cookie strings using Windows DPAPI (Data Protection API).
    /// Encrypted values are tied to the current Windows user account, so stolen
    /// settings files are useless on another machine or user profile.
    /// </summary>
    public static class CookieProtector {
        private const string EncryptedPrefix = "DPAPI:";

        /// <summary>
        /// Encrypts a plaintext cookie string. Returns a DPAPI-prefixed Base64 string.
        /// Returns null if input is null/empty.
        /// </summary>
        public static string Protect(string plaintext) {
            if (string.IsNullOrEmpty(plaintext)) return null;

            try {
                var bytes = Encoding.UTF8.GetBytes(plaintext);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return EncryptedPrefix + Convert.ToBase64String(encrypted);
            } catch (Exception ex) {
                Logger.Warning($"[CookieProtector] Failed to encrypt cookie: {ex.Message}");
                // Fallback: return plaintext so the app still works
                return plaintext;
            }
        }

        /// <summary>
        /// Decrypts a cookie string. Handles both encrypted (DPAPI-prefixed) and
        /// legacy plaintext cookies for backward compatibility.
        /// </summary>
        public static string Unprotect(string value) {
            if (string.IsNullOrEmpty(value)) return null;

            // If it's not encrypted (legacy plaintext), return as-is
            if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) {
                return value;
            }

            try {
                var base64 = value.Substring(EncryptedPrefix.Length);
                var encrypted = Convert.FromBase64String(base64);
                var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            } catch (CryptographicException ex) {
                Logger.Warning($"[CookieProtector] Failed to decrypt cookie (may belong to a different user): {ex.Message}");
                return null;
            } catch (Exception ex) {
                Logger.Warning($"[CookieProtector] Failed to decrypt cookie: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns true if the value is already encrypted with DPAPI.
        /// </summary>
        public static bool IsEncrypted(string value) {
            return !string.IsNullOrEmpty(value) && value.StartsWith(EncryptedPrefix, StringComparison.Ordinal);
        }
    }
}
