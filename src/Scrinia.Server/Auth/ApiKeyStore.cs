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

        string keyHash = HashKey(rawKey);
        string keyId = Guid.NewGuid().ToString("N")[..16];
        string permissionsJson = JsonSerializer.Serialize(permissions ?? []);

        using var transaction = _db.BeginTransaction();

        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO api_keys (id, key_hash, user_id, permissions, label, created_at)
                VALUES ($id, $hash, $userId, $permissions, $label, $createdAt);
                """;
            cmd.Parameters.AddWithValue("$id", keyId);
            cmd.Parameters.AddWithValue("$hash", keyHash);
            cmd.Parameters.AddWithValue("$userId", userId);
            cmd.Parameters.AddWithValue("$permissions", permissionsJson);
            cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
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

        return (rawKey, keyId, userId);
    }

    public sealed record KeyInfo(string KeyId, string UserId, string[] Stores, string[] Permissions);

    /// <summary>
    /// Validates a raw API key. Returns full key info if valid, null if invalid/revoked.
    /// Updates last_used_at on success.
    /// </summary>
    public KeyInfo? ValidateKey(string rawKey)
    {
        string keyHash = HashKey(rawKey);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, permissions, revoked
            FROM api_keys
            WHERE key_hash = $hash;
            """;
        cmd.Parameters.AddWithValue("$hash", keyHash);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        bool revoked = reader.GetInt64(3) != 0;
        if (revoked) return null;

        string keyId = reader.GetString(0);
        string userId = reader.GetString(1);
        string permissionsJson = reader.GetString(2);

        string[] permissions = JsonSerializer.Deserialize<string[]>(permissionsJson) ?? [];
        string[] stores = GetStoresForKey(keyId);

        // Update last_used_at
        using var updateCmd = _db.CreateCommand();
        updateCmd.CommandText = "UPDATE api_keys SET last_used_at = $now WHERE id = $id;";
        updateCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        updateCmd.Parameters.AddWithValue("$id", keyId);
        updateCmd.ExecuteNonQuery();

        return new KeyInfo(keyId, userId, stores, permissions);
    }

    /// <summary>Revokes a key by its ID.</summary>
    public bool RevokeKey(string keyId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE api_keys SET revoked = 1 WHERE id = $id AND revoked = 0;";
        cmd.Parameters.AddWithValue("$id", keyId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Returns true if any API keys exist (for bootstrap detection).</summary>
    public bool HasAnyKeys()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM api_keys;";
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public sealed record KeySummary(
        string Id, string UserId, string[] Stores, string[] Permissions,
        string? Label, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);

    /// <summary>Lists all API keys (for management endpoints).</summary>
    public List<KeySummary> ListKeys()
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
            var createdAt = DateTimeOffset.Parse(reader.GetString(4));
            DateTimeOffset? lastUsedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5));
            bool revoked = reader.GetInt64(6) != 0;
            string[] stores = GetStoresForKey(keyId);

            result.Add(new KeySummary(keyId, userId, stores, permissions, label, createdAt, lastUsedAt, revoked));
        }

        return result;
    }

    /// <summary>Gets a single key's details by ID.</summary>
    public KeySummary? GetKey(string keyId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, permissions, label, created_at, last_used_at, revoked FROM api_keys WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", keyId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        string userId = reader.GetString(1);
        string[] permissions = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [];
        string? label = reader.IsDBNull(3) ? null : reader.GetString(3);
        var createdAt = DateTimeOffset.Parse(reader.GetString(4));
        DateTimeOffset? lastUsedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5));
        bool revoked = reader.GetInt64(6) != 0;
        string[] stores = GetStoresForKey(keyId);

        return new KeySummary(keyId, userId, stores, permissions, label, createdAt, lastUsedAt, revoked);
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

    internal static byte[] HashKeyBytes(string rawKey)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(rawKey);
        return SHA256.HashData(bytes);
    }

    private static string HashKey(string rawKey) =>
        Convert.ToHexStringLower(HashKeyBytes(rawKey));

    public void Dispose() => _db.Dispose();
}
