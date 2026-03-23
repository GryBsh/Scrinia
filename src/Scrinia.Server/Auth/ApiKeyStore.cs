using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scrinia.Server.Auth;

/// <summary>
/// SQLite-backed API key store. Stores only SHA-256 hashes of keys.
/// Raw keys are returned once on creation and never stored.
/// Uses connection-per-operation with SQLite pooling (WAL mode).
/// </summary>
public sealed class ApiKeyStore
{
    private readonly string _connString;

    public ApiKeyStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = true,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        // Set WAL mode once during initialization
        using (var conn = new SqliteConnection(_connString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        Initialize();
    }

    /// <summary>Retries an operation up to 3 times on SQLITE_BUSY.</summary>
    private T RetryOnBusy<T>(Func<T> operation)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return operation(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < 3) // 5 = SQLITE_BUSY
            {
                Thread.Sleep(100 * (1 << attempt));
            }
        }
    }

    /// <summary>Retries a void operation up to 3 times on SQLITE_BUSY.</summary>
    private void RetryOnBusy(Action operation)
    {
        RetryOnBusy<object?>(() => { operation(); return null; });
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS api_keys (
                id           TEXT PRIMARY KEY,
                key_hash     TEXT NOT NULL UNIQUE,
                user_id      TEXT NOT NULL,
                permissions  TEXT NOT NULL DEFAULT '[]',
                label        TEXT,
                created_at   TEXT NOT NULL,
                last_used_at TEXT,
                revoked      INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS key_stores (
                key_id     TEXT NOT NULL REFERENCES api_keys(id) ON DELETE CASCADE,
                store_name TEXT NOT NULL,
                PRIMARY KEY (key_id, store_name)
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrate: add salt column for per-key salted hashing
        using var colCheck = conn.CreateCommand();
        colCheck.CommandText = "PRAGMA table_info(api_keys);";
        bool hasSalt = false;
        using (var reader = colCheck.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1) == "salt") { hasSalt = true; break; }

        if (!hasSalt)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE api_keys ADD COLUMN salt TEXT;";
            alter.ExecuteNonQuery();
        }

        // Migrate: add key_prefix column for O(1) prefix-based lookup
        bool hasKeyPrefix = false;
        using var prefixCheck = conn.CreateCommand();
        prefixCheck.CommandText = "PRAGMA table_info(api_keys);";
        using (var prefixReader = prefixCheck.ExecuteReader())
            while (prefixReader.Read())
                if (prefixReader.GetString(1) == "key_prefix") { hasKeyPrefix = true; break; }

        if (!hasKeyPrefix)
        {
            using var alterPrefix = conn.CreateCommand();
            alterPrefix.CommandText = "ALTER TABLE api_keys ADD COLUMN key_prefix TEXT;";
            alterPrefix.ExecuteNonQuery();
        }

        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_key_prefix ON api_keys(key_prefix) WHERE revoked = 0;";
        idxCmd.ExecuteNonQuery();

        // Enable foreign keys
        using var fkCmd = conn.CreateCommand();
        fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
        fkCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates a new API key. Returns the raw key (shown once) and key metadata.
    /// Key format: scri_ + 32 random bytes base64url-encoded.
    /// </summary>
    public (string RawKey, string KeyId, string UserId) CreateKey(
        string userId, string[] stores, string[]? permissions = null, string? label = null)
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(32);
        string rawKey = "scri_" + Convert.ToBase64String(randomBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string saltHex = Convert.ToHexStringLower(salt);
        string keyHash = HashKey(rawKey, salt);
        string keyId = Guid.NewGuid().ToString("N")[..16];
        string permissionsJson = JsonSerializer.Serialize(permissions ?? []);
        string keyPrefix = rawKey[..8];

        RetryOnBusy(() =>
        {
            using var conn = new SqliteConnection(_connString);
            conn.Open();

            using var transaction = conn.BeginTransaction();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        INSERT INTO api_keys (id, key_hash, user_id, permissions, label, created_at, salt, key_prefix)
                        VALUES ($id, $hash, $userId, $permissions, $label, $createdAt, $salt, $keyPrefix);
                        """;
                    cmd.Parameters.AddWithValue("$id", keyId);
                    cmd.Parameters.AddWithValue("$hash", keyHash);
                    cmd.Parameters.AddWithValue("$userId", userId);
                    cmd.Parameters.AddWithValue("$permissions", permissionsJson);
                    cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("$salt", saltHex);
                    cmd.Parameters.AddWithValue("$keyPrefix", keyPrefix);
                    cmd.ExecuteNonQuery();
                }

                foreach (string store in stores)
                {
                    using var storeCmd = conn.CreateCommand();
                    storeCmd.Transaction = transaction;
                    storeCmd.CommandText = "INSERT INTO key_stores (key_id, store_name) VALUES ($keyId, $store);";
                    storeCmd.Parameters.AddWithValue("$keyId", keyId);
                    storeCmd.Parameters.AddWithValue("$store", store);
                    storeCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw; // RetryOnBusy will catch and retry
            }
        });

        return (rawKey, keyId, userId);
    }

    public sealed record KeyInfo(string KeyId, string UserId, string[] Stores, string[] Permissions);

    /// <summary>
    /// Validates a raw API key. Returns full key info if valid, null if invalid/revoked.
    /// Updates last_used_at on success.
    /// Uses a single connection for atomicity of the SELECT + UPDATE.
    /// </summary>
    public KeyInfo? ValidateKey(string rawKey)
    {
        if (rawKey.Length < 8)
            return null;

        using var conn = new SqliteConnection(_connString);
        conn.Open();

        // Enable foreign keys on this connection
        using (var fkCmd = conn.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
            fkCmd.ExecuteNonQuery();
        }

        // Hash matching
        string? matchedKeyId = null;
        string? matchedUserId = null;
        string? matchedPermissionsJson = null;

        var prefix = rawKey[..8];

        // Fast path: prefix-indexed lookup
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, user_id, permissions, salt, key_hash
                FROM api_keys
                WHERE key_prefix = $prefix AND revoked = 0;
                """;
            cmd.Parameters.AddWithValue("$prefix", prefix);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string storedHash = reader.GetString(4);
                string? saltHex = reader.IsDBNull(3) ? null : reader.GetString(3);

                byte[]? salt = saltHex is not null ? Convert.FromHexString(saltHex) : null;
                byte[] candidateHash = HashKeyBytes(rawKey, salt);
                byte[] storedHashBytes = Convert.FromHexString(storedHash);

                if (!CryptographicOperations.FixedTimeEquals(candidateHash, storedHashBytes))
                    continue;

                matchedKeyId = reader.GetString(0);
                matchedUserId = reader.GetString(1);
                matchedPermissionsJson = reader.GetString(2);
                break;
            }
        }

        // Slow path: legacy keys without prefix (key_prefix IS NULL)
        if (matchedKeyId is null)
        {
            using var legacyCmd = conn.CreateCommand();
            legacyCmd.CommandText = """
                SELECT id, user_id, permissions, salt, key_hash
                FROM api_keys
                WHERE key_prefix IS NULL AND revoked = 0;
                """;

            using var legacyReader = legacyCmd.ExecuteReader();
            while (legacyReader.Read())
            {
                string storedHash = legacyReader.GetString(4);
                string? saltHex = legacyReader.IsDBNull(3) ? null : legacyReader.GetString(3);

                byte[]? salt = saltHex is not null ? Convert.FromHexString(saltHex) : null;
                byte[] candidateHash = HashKeyBytes(rawKey, salt);
                byte[] storedHashBytes = Convert.FromHexString(storedHash);

                if (!CryptographicOperations.FixedTimeEquals(candidateHash, storedHashBytes))
                    continue;

                matchedKeyId = legacyReader.GetString(0);
                matchedUserId = legacyReader.GetString(1);
                matchedPermissionsJson = legacyReader.GetString(2);
                break;
            }
        }

        if (matchedKeyId is null) return null;

        string[] permissions = JsonSerializer.Deserialize<string[]>(matchedPermissionsJson!) ?? [];
        string[] stores = GetStoresForKey(conn, matchedKeyId);

        // Update last_used_at
        RetryOnBusy(() =>
        {
            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE api_keys SET last_used_at = $now WHERE id = $id;";
            updateCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            updateCmd.Parameters.AddWithValue("$id", matchedKeyId);
            updateCmd.ExecuteNonQuery();
        });

        return new KeyInfo(matchedKeyId, matchedUserId!, stores, permissions);
    }

    /// <summary>Revokes a key by its ID.</summary>
    public bool RevokeKey(string keyId)
    {
        return RetryOnBusy(() =>
        {
            using var conn = new SqliteConnection(_connString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE api_keys SET revoked = 1 WHERE id = $id AND revoked = 0;";
            cmd.Parameters.AddWithValue("$id", keyId);
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>Returns true if any API keys exist (for bootstrap detection).</summary>
    public bool HasAnyKeys()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM api_keys;";
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public sealed record KeySummary(
        string Id, string UserId, string[] Stores, string[] Permissions,
        string? Label, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);

    /// <summary>Lists all API keys (for management endpoints).</summary>
    public List<KeySummary> ListKeys()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();

        var result = new List<KeySummary>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, permissions, label, created_at, last_used_at, revoked FROM api_keys ORDER BY created_at;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string keyId = reader.GetString(0);
            string userId = reader.GetString(1);
            string[] permissions = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [];
            string? label = reader.IsDBNull(3) ? null : reader.GetString(3);
            var createdAt = DateTimeOffset.TryParse(reader.GetString(4), out var ca) ? ca : DateTimeOffset.UtcNow;
            DateTimeOffset? lastUsedAt = reader.IsDBNull(5) ? null
                : DateTimeOffset.TryParse(reader.GetString(5), out var lu) ? lu : DateTimeOffset.UtcNow;
            bool revoked = reader.GetInt64(6) != 0;
            string[] stores = GetStoresForKey(conn, keyId);

            result.Add(new KeySummary(keyId, userId, stores, permissions, label, createdAt, lastUsedAt, revoked));
        }

        return result;
    }

    /// <summary>Gets a single key's details by ID.</summary>
    public KeySummary? GetKey(string keyId)
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, permissions, label, created_at, last_used_at, revoked FROM api_keys WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", keyId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        string userId = reader.GetString(1);
        string[] permissions = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [];
        string? label = reader.IsDBNull(3) ? null : reader.GetString(3);
        var createdAt = DateTimeOffset.TryParse(reader.GetString(4), out var ca2) ? ca2 : DateTimeOffset.UtcNow;
        DateTimeOffset? lastUsedAt = reader.IsDBNull(5) ? null
            : DateTimeOffset.TryParse(reader.GetString(5), out var lu2) ? lu2 : DateTimeOffset.UtcNow;
        bool revoked = reader.GetInt64(6) != 0;
        string[] stores = GetStoresForKey(conn, keyId);

        return new KeySummary(keyId, userId, stores, permissions, label, createdAt, lastUsedAt, revoked);
    }

    private static string[] GetStoresForKey(SqliteConnection conn, string keyId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT store_name FROM key_stores WHERE key_id = $keyId ORDER BY store_name;";
        cmd.Parameters.AddWithValue("$keyId", keyId);

        var stores = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            stores.Add(reader.GetString(0));
        return stores.ToArray();
    }

    internal static byte[] HashKeyBytes(string rawKey, byte[]? salt = null)
    {
        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(rawKey);
        if (salt is null)
            return SHA256.HashData(keyBytes);

        byte[] salted = new byte[salt.Length + keyBytes.Length];
        salt.CopyTo(salted, 0);
        keyBytes.CopyTo(salted, salt.Length);
        return SHA256.HashData(salted);
    }

    private static string HashKey(string rawKey, byte[]? salt = null) =>
        Convert.ToHexStringLower(HashKeyBytes(rawKey, salt));
}
