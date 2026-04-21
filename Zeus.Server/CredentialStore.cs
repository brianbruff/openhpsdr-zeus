using System.Security.Cryptography;
using System.Text;
using LiteDB;

namespace Zeus.Server;

public sealed class CredentialStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<StoredCredential> _credentials;
    private readonly ILogger<CredentialStore> _log;

    public CredentialStore(ILogger<CredentialStore> log)
    {
        _log = log;
        var dbPath = GetDatabasePath();
        var dbPassword = GetOrCreateDatabasePassword();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _log.LogInformation("Created credential store directory: {Dir}", dir);
        }

        var connectionString = $"Filename={dbPath};Password={dbPassword};Connection=shared";
        _db = new LiteDatabase(connectionString);
        _credentials = _db.GetCollection<StoredCredential>("credentials");
        _credentials.EnsureIndex(x => x.Service, unique: true);

        _log.LogInformation("CredentialStore initialized at {Path}", dbPath);
    }

    public async Task<StoredCredential?> GetAsync(string service, CancellationToken ct = default)
    {
        return await Task.Run(() => _credentials.FindOne(x => x.Service == service), ct);
    }

    public async Task SetAsync(string service, string username, string password, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            // Find existing credential for this service
            var existing = _credentials.FindOne(x => x.Service == service);

            if (existing != null)
            {
                // Update existing
                existing.Username = username;
                existing.Password = password;
                existing.UpdatedUtc = DateTime.UtcNow;
                _credentials.Update(existing);
            }
            else
            {
                // Insert new
                var cred = new StoredCredential
                {
                    Service = service,
                    Username = username,
                    Password = password,
                    UpdatedUtc = DateTime.UtcNow
                };
                _credentials.Insert(cred);
            }
        }, ct);

        _log.LogInformation("Stored credentials for service={Service} username={User}", service, username);
    }

    public async Task DeleteAsync(string service, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var deleted = _credentials.DeleteMany(x => x.Service == service);
            if (deleted > 0)
            {
                _log.LogInformation("Deleted credentials for service={Service}", service);
            }
        }, ct);
    }

    public void Dispose()
    {
        _db?.Dispose();
    }

    private static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        var zeusDir = Path.Combine(appDataDir, "Zeus");
        return Path.Combine(zeusDir, "zeus.db");
    }

    private string GetOrCreateDatabasePassword()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        var zeusDir = Path.Combine(appDataDir, "Zeus");
        var keyPath = Path.Combine(zeusDir, ".dbkey");

        if (File.Exists(keyPath))
        {
            try
            {
                return File.ReadAllText(keyPath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read existing database key; generating new one");
            }
        }

        // Generate a new random key
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        try
        {
            if (!Directory.Exists(zeusDir))
            {
                Directory.CreateDirectory(zeusDir);
            }

            File.WriteAllText(keyPath, key);

            // Set file permissions to 0600 on Unix-like systems
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to set Unix file permissions on database key");
                }
            }

            _log.LogInformation("Created new database key at {Path}", keyPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist database key; using ephemeral key");
        }

        return key;
    }
}

public sealed class StoredCredential
{
    public int Id { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
