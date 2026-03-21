using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scrinia.Server.Auth;

/// <summary>
/// SQLite-backed API key store. Stores only SHA-256 hashes of keys.
/// Raw keys are returned once on creation and never stored.
/// </summary>
public sealed class ApiKeyStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ReaderWriterLockSlim _lock = new();

    public ApiKeyStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _db.CreateCommand();
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
        using var colCheck = _db.CreateCommand();
        colCheck.CommandText = "PRAGMA table_info(api_keys);";
        bool hasSalt = false;
        using (var reader = colCheck.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1) == "salt") { hasSalt = true; break; }

        if (!hasSalt)
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE api_keys ADD COLUMN salt TEXT;";
            alter.ExecuteNonQuery();
        }

        // Enable foreign keys
        using var fkCmd = _db.CreateCommand();
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

        _lock.EnterWriteLock();
        try
        {
            using var transaction = _db.BeginTransaction();

            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO api_keys (id, key_hash, user_id, permissions, label, created_at, salt)
                    VALUES ($id, $hash, $userId, $permissions, $label, $createdAt, $salt);
                    """;
                cmd.Parameters.AddWithValue("$id", keyId);
                cmd.Parameters.AddWithValue("$hash", keyHash);
                cmd.Parameters.AddWithValue("$userId", userId);
                cmd.Parameters.AddWithValue("$permissions", permissionsJson);
                cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$salt", saltHex);
                cmd.ExecuteNonQuery();
            }

            foreach (string store in stores)
            {
                using var storeCmd = _db.CreateCommand();
                storeCmd.Transaction = transaction;
                storeCmd.CommandText = "INSERT INTO key_stores (key_id, store_name) VALUES ($keyId, $store);";
                storeCmd.Parameters.AddWithValue("$keyId", keyId);
                storeCmd.Parameters.AddWithValue("$store", store);
                storeCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.ExitWriteLock(); }

        return (rawKey, keyId, userId);
    }

    public sealed record KeyInfo(string KeyId, string UserId, string[] Stores, string[] Permissions);

    /// <summary>
    /// Validates a raw API key. Returns full key info if valid, null if invalid/revoked.
    /// Updates last_used_at on success.
    /// </summary>
    public KeyInfo? ValidateKey(string rawKey)
    {
        _lock.EnterReadLock();
        string? matchedKeyId = null;
        string? matchedUserId = null;
        string? matchedPermissionsJson = null;
        try
        {
            using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, permissions, revoked, salt, key_hash
            FROM api_keys
            WHERE revoked = 0;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string storedHash = reader.GetString(5);
            string? saltHex = reader.IsDBNull(4) ? null : reader.GetString(4);

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
        finally { _lock.ExitReadLock(); }

        if (matchedKeyId is null) return null;

        string[] permissions = JsonSerializer.Deserialize<string[]>(matchedPermissionsJson!) ?? [];

        // GetStoresForKey under read lock to prevent race with concurrent key deletion
        _lock.EnterReadLock();
        string[] stores;
        try { stores = GetStoresForKey(matchedKeyId); }
        finally { _lock.ExitReadLock(); }

        // Update last_used_at (write operation — needs write lock)
        _lock.EnterWriteLock();
        try
        {
            using var updateCmd = _db.CreateCommand();
            updateCmd.CommandText = "UPDATE api_keys SET last_used_at = $now WHERE id = $id;";
            updateCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            updateCmd.Parameters.AddWithValue("$id", matchedKeyId);
            updateCmd.ExecuteNonQuery();
        }
        finally { _lock.ExitWriteLock(); }

        return new KeyInfo(matchedKeyId, matchedUserId!, stores, permissions);
    }

    /// <summary>Revokes a key by its ID.</summary>
    public bool RevokeKey(string keyId)
    {
        _lock.EnterWriteLock();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE api_keys SET revoked = 1 WHERE id = $id AND revoked = 0;";
            cmd.Parameters.AddWithValue("$id", keyId);
            return cmd.ExecuteNonQuery() > 0;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Returns true if any API keys exist (for bootstrap detection).</summary>
    public bool HasAnyKeys()
    {
        _lock.EnterReadLock();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM api_keys;";
            return (long)cmd.ExecuteScalar()! > 0;
        }
        finally { _lock.ExitReadLock(); }
    }

    public sealed record KeySummary(
        string Id, string UserId, string[] Stores, string[] Permissions,
        string? Label, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);

    /// <summary>Lists all API keys (for management endpoints).</summary>
    public List<KeySummary> ListKeys()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<KeySummary>();
            using var cmd = _db.CreateCommand();
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
                string[] stores = GetStoresForKey(keyId);

                result.Add(new KeySummary(keyId, userId, stores, permissions, label, createdAt, lastUsedAt, revoked));
            }

            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Gets a single key's details by ID.</summary>
    public KeySummary? GetKey(string keyId)
    {
        _lock.EnterReadLock();
        try
        {
            using var cmd = _db.CreateCommand();
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
            string[] stores = GetStoresForKey(keyId);

            return new KeySummary(keyId, userId, stores, permissions, label, createdAt, lastUsedAt, revoked);
        }
        finally { _lock.ExitReadLock(); }
    }

    private string[] GetStoresForKey(string keyId)
    {
        using var cmd = _db.CreateCommand();
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

    public void Dispose()
    {
        _lock.Dispose();
        _db.Dispose();
    }
}
