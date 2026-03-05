using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Transport
{
    public sealed class AuthSessionService
    {
        private const string AuthFileName = "auth.json";
        private const string SessionFileName = "auth_sessions.json";
        private const int HashByteLength = 32;
        private const int SaltByteLength = 16;
        private const int Iterations = 120000;
        private const int SessionHours = 24 * 365 * 5;
        private const int MaxFailedAttempts = 5;
        private const int LockoutSeconds = 30;
        private const int TokenByteLength = 32;

        private readonly object syncRoot = new object();
        private readonly Dictionary<string, DateTime> sessionsByToken = new Dictionary<string, DateTime>();
        private readonly string authFilePath;
        private readonly string sessionFilePath;
        private AuthConfigData config;
        private DateTime configWriteTimeUtc;
        private int failedLoginCount;
        private DateTime lockoutUntilUtc;

        public AuthSessionService(string remotePostprocessRootPath)
        {
            if (string.IsNullOrWhiteSpace(remotePostprocessRootPath))
            {
                throw new ArgumentException("remotePostprocessRootPath is required.", nameof(remotePostprocessRootPath));
            }

            authFilePath = Path.Combine(remotePostprocessRootPath, AuthFileName);
            sessionFilePath = Path.Combine(remotePostprocessRootPath, SessionFileName);
        }

        public bool RequiresSetup
        {
            get
            {
                lock (syncRoot)
                {
                    try
                    {
                        RefreshConfigNoLock();
                        return config == null;
                    }
                    catch
                    {
                        ResetConfigNoLock();
                        return true;
                    }
                }
            }
        }

        public bool TrySetup(string password, out string error)
        {
            error = string.Empty;
            lock (syncRoot)
            {
                try
                {
                    RefreshConfigNoLock();
                    if (config != null)
                    {
                        error = "already_configured";
                        return false;
                    }

                    if (!ValidatePassword(password, out error))
                    {
                        return false;
                    }

                    byte[] salt = CreateRandomBytes(SaltByteLength);
                    byte[] hash = DeriveHash(password, salt, Iterations, HashByteLength);
                    var data = new AuthConfigData
                    {
                        version = 1,
                        iterations = Iterations,
                        keyLength = HashByteLength,
                        saltBase64 = Convert.ToBase64String(salt),
                        hashBase64 = Convert.ToBase64String(hash),
                        createdAtUtc = DateTime.UtcNow.ToString("O")
                    };

                    SaveConfigNoLock(data);
                    config = data;
                    failedLoginCount = 0;
                    lockoutUntilUtc = DateTime.MinValue;
                    sessionsByToken.Clear();
                    SaveSessionsNoLock(DateTime.UtcNow);
                    return true;
                }
                catch (Exception ex)
                {
                    error = MapSetupErrorCode(ex);
                    Debug.LogError("[URP Remote PP] setup_failed in AuthSessionService.TrySetup");
                    Debug.LogException(ex);
                    return false;
                }
            }
        }

        public bool TryLogin(string password, out string token, out int expiresInSeconds, out string error)
        {
            token = string.Empty;
            expiresInSeconds = 0;
            error = string.Empty;

            lock (syncRoot)
            {
                try
                {
                    RefreshConfigNoLock();
                    if (config == null)
                    {
                        error = "setup_required";
                        return false;
                    }

                    DateTime now = DateTime.UtcNow;
                    if (now < lockoutUntilUtc)
                    {
                        error = "locked";
                        return false;
                    }

                    if (!ValidatePassword(password, out error))
                    {
                        return false;
                    }

                    if (!VerifyPasswordNoLock(password))
                    {
                        failedLoginCount += 1;
                        if (failedLoginCount >= MaxFailedAttempts)
                        {
                            lockoutUntilUtc = now.AddSeconds(LockoutSeconds);
                            failedLoginCount = 0;
                            error = "locked";
                        }
                        else
                        {
                            error = "invalid_credentials";
                        }

                        return false;
                    }

                    failedLoginCount = 0;
                    lockoutUntilUtc = DateTime.MinValue;
                    PruneExpiredSessionsNoLock(now);

                    token = CreateToken();
                    DateTime expiresAt = now.AddHours(SessionHours);
                    sessionsByToken[token] = expiresAt;
                    SaveSessionsNoLock(now);
                    expiresInSeconds = (int)Math.Max(1, Math.Round((expiresAt - now).TotalSeconds));
                    return true;
                }
                catch (Exception ex)
                {
                    error = MapLoginErrorCode(ex);
                    Debug.LogError("[URP Remote PP] login_failed in AuthSessionService.TryLogin");
                    Debug.LogException(ex);
                    return false;
                }
            }
        }

        public bool ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            lock (syncRoot)
            {
                try
                {
                    RefreshConfigNoLock();
                    if (config == null)
                    {
                        return false;
                    }

                    DateTime now = DateTime.UtcNow;
                    int beforePruneCount = sessionsByToken.Count;
                    PruneExpiredSessionsNoLock(now);
                    if (beforePruneCount != sessionsByToken.Count)
                    {
                        SaveSessionsNoLock(now);
                    }

                    if (!sessionsByToken.TryGetValue(token, out DateTime expiryUtc))
                    {
                        return false;
                    }

                    if (expiryUtc <= now)
                    {
                        sessionsByToken.Remove(token);
                        SaveSessionsNoLock(now);
                        return false;
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool RevokeToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            lock (syncRoot)
            {
                try
                {
                    bool removed = sessionsByToken.Remove(token);
                    if (removed)
                    {
                        SaveSessionsNoLock(DateTime.UtcNow);
                    }

                    return removed;
                }
                catch
                {
                    return false;
                }
            }
        }

        public int GetRetryAfterSeconds()
        {
            lock (syncRoot)
            {
                try
                {
                    DateTime now = DateTime.UtcNow;
                    if (now >= lockoutUntilUtc)
                    {
                        return 0;
                    }

                    return (int)Math.Ceiling((lockoutUntilUtc - now).TotalSeconds);
                }
                catch
                {
                    return 0;
                }
            }
        }

        private void RefreshConfigNoLock()
        {
            try
            {
                string path = GetAuthPath();
                if (!File.Exists(path))
                {
                    ResetConfigNoLock();
                    return;
                }

                DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);
                if (config != null && writeTimeUtc == configWriteTimeUtc)
                {
                    return;
                }

                string json = File.ReadAllText(path);
                AuthConfigData loaded = JsonUtility.FromJson<AuthConfigData>(json);
                if (!IsValidConfig(loaded))
                {
                    ResetConfigNoLock();
                    return;
                }

                config = loaded;
                configWriteTimeUtc = writeTimeUtc;
                sessionsByToken.Clear();
                failedLoginCount = 0;
                lockoutUntilUtc = DateTime.MinValue;
                LoadSessionsNoLock(DateTime.UtcNow);
            }
            catch
            {
                ResetConfigNoLock();
            }
        }

        private void SaveConfigNoLock(AuthConfigData data)
        {
            string path = GetAuthPath();
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            Directory.CreateDirectory(dir);
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            configWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        }

        private void ResetConfigNoLock()
        {
            config = null;
            configWriteTimeUtc = DateTime.MinValue;
            sessionsByToken.Clear();
            failedLoginCount = 0;
            lockoutUntilUtc = DateTime.MinValue;
        }

        private void LoadSessionsNoLock(DateTime nowUtc)
        {
            sessionsByToken.Clear();
            if (config == null || !File.Exists(sessionFilePath))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(sessionFilePath);
                AuthSessionStoreData store = JsonUtility.FromJson<AuthSessionStoreData>(json);
                if (store == null)
                {
                    return;
                }

                if (!string.Equals(store.saltBase64 ?? string.Empty, config.saltBase64 ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(store.hashBase64 ?? string.Empty, config.hashBase64 ?? string.Empty, StringComparison.Ordinal))
                {
                    DeleteSessionFileNoLock();
                    return;
                }

                List<AuthSessionEntryData> entries = store.sessions ?? new List<AuthSessionEntryData>();
                for (int i = 0; i < entries.Count; i++)
                {
                    AuthSessionEntryData entry = entries[i];
                    string token = (entry?.token ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(token))
                    {
                        continue;
                    }

                    if (!DateTime.TryParse(
                        entry.expiresAtUtc ?? string.Empty,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out DateTime expiryUtc))
                    {
                        continue;
                    }

                    expiryUtc = expiryUtc.ToUniversalTime();
                    if (expiryUtc <= nowUtc)
                    {
                        continue;
                    }

                    sessionsByToken[token] = expiryUtc;
                }
            }
            catch
            {
                sessionsByToken.Clear();
            }
        }

        private void SaveSessionsNoLock(DateTime nowUtc)
        {
            if (config == null)
            {
                DeleteSessionFileNoLock();
                return;
            }

            PruneExpiredSessionsNoLock(nowUtc);

            string dir = Path.GetDirectoryName(sessionFilePath) ?? string.Empty;
            Directory.CreateDirectory(dir);

            var store = new AuthSessionStoreData
            {
                saltBase64 = config.saltBase64 ?? string.Empty,
                hashBase64 = config.hashBase64 ?? string.Empty,
                sessions = new List<AuthSessionEntryData>()
            };

            foreach (KeyValuePair<string, DateTime> pair in sessionsByToken)
            {
                store.sessions.Add(new AuthSessionEntryData
                {
                    token = pair.Key,
                    expiresAtUtc = pair.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                });
            }

            string json = JsonUtility.ToJson(store, false);
            File.WriteAllText(sessionFilePath, json);
        }

        private void DeleteSessionFileNoLock()
        {
            try
            {
                if (File.Exists(sessionFilePath))
                {
                    File.Delete(sessionFilePath);
                }
            }
            catch
            {
            }
        }

        private void PruneExpiredSessionsNoLock(DateTime now)
        {
            if (sessionsByToken.Count == 0)
            {
                return;
            }

            var staleTokens = new List<string>();
            foreach (KeyValuePair<string, DateTime> pair in sessionsByToken)
            {
                if (pair.Value <= now)
                {
                    staleTokens.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleTokens.Count; i++)
            {
                sessionsByToken.Remove(staleTokens[i]);
            }
        }

        private bool VerifyPasswordNoLock(string password)
        {
            if (config == null)
            {
                return false;
            }

            byte[] salt;
            byte[] expectedHash;
            try
            {
                salt = Convert.FromBase64String(config.saltBase64 ?? string.Empty);
                expectedHash = Convert.FromBase64String(config.hashBase64 ?? string.Empty);
            }
            catch
            {
                return false;
            }

            if (salt.Length == 0 || expectedHash.Length == 0)
            {
                return false;
            }

            byte[] actualHash = DeriveHash(password, salt, Math.Max(1000, config.iterations), Math.Max(16, config.keyLength));
            return ConstantTimeEquals(expectedHash, actualHash);
        }

        private static bool ValidatePassword(string password, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(password))
            {
                error = "password_required";
                return false;
            }

            if (password.Length < 8)
            {
                error = "password_too_short";
                return false;
            }

            if (password.Length > 128)
            {
                error = "password_too_long";
                return false;
            }

            return true;
        }

        private static string MapSetupErrorCode(Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is IOException)
            {
                return "setup_io_failed";
            }

            if (ex is CryptographicException)
            {
                return "setup_crypto_failed";
            }

            return "setup_failed";
        }

        private static string MapLoginErrorCode(Exception ex)
        {
            if (ex is CryptographicException)
            {
                return "login_crypto_failed";
            }

            return "login_failed";
        }

        private static byte[] DeriveHash(string password, byte[] salt, int iterations, int keyLength)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations))
            {
                return pbkdf2.GetBytes(keyLength);
            }
        }

        private static byte[] CreateRandomBytes(int size)
        {
            var bytes = new byte[size];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return bytes;
        }

        private static string CreateToken()
        {
            byte[] bytes = CreateRandomBytes(TokenByteLength);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static bool ConstantTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }

            return diff == 0;
        }

        private static bool IsValidConfig(AuthConfigData data)
        {
            return data != null
                && data.iterations >= 1000
                && data.keyLength >= 16
                && !string.IsNullOrWhiteSpace(data.saltBase64)
                && !string.IsNullOrWhiteSpace(data.hashBase64);
        }

        private string GetAuthPath()
        {
            return authFilePath;
        }

        [Serializable]
        private sealed class AuthConfigData
        {
            public int version = 1;
            public int iterations = Iterations;
            public int keyLength = HashByteLength;
            public string saltBase64 = string.Empty;
            public string hashBase64 = string.Empty;
            public string createdAtUtc = string.Empty;
        }

        [Serializable]
        private sealed class AuthSessionStoreData
        {
            public string saltBase64 = string.Empty;
            public string hashBase64 = string.Empty;
            public List<AuthSessionEntryData> sessions = new List<AuthSessionEntryData>();
        }

        [Serializable]
        private sealed class AuthSessionEntryData
        {
            public string token = string.Empty;
            public string expiresAtUtc = string.Empty;
        }
    }
}
