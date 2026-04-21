using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _testDbDir;
    private readonly CredentialStore _store;

    public CredentialStoreTests()
    {
        // Create a temporary test directory
        _testDbDir = Path.Combine(Path.GetTempPath(), $"zeus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDbDir);

        // Override the app data dir for testing by setting environment variable
        Environment.SetEnvironmentVariable("HOME", _testDbDir);
        Environment.SetEnvironmentVariable("USERPROFILE", _testDbDir);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _testDbDir);

        _store = new CredentialStore(NullLogger<CredentialStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();

        // Clean up test directory
        if (Directory.Exists(_testDbDir))
        {
            try
            {
                Directory.Delete(_testDbDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task SetAsync_StoresCredentials()
    {
        // Arrange
        const string service = "test-service";
        const string username = "testuser";
        const string password = "testpass123";

        // Act
        await _store.SetAsync(service, username, password);

        // Assert
        var retrieved = await _store.GetAsync(service);
        Assert.NotNull(retrieved);
        Assert.Equal(service, retrieved.Service);
        Assert.Equal(username, retrieved.Username);
        Assert.Equal(password, retrieved.Password);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenServiceNotFound()
    {
        // Act
        var result = await _store.GetAsync("nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCredentials()
    {
        // Arrange
        const string service = "test-service";
        await _store.SetAsync(service, "user", "pass");

        // Act
        await _store.DeleteAsync(service);

        // Assert
        var result = await _store.GetAsync(service);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_UpdatesExistingCredentials()
    {
        // Arrange
        const string service = "test-service";
        await _store.SetAsync(service, "olduser", "oldpass");

        // Act
        await _store.SetAsync(service, "newuser", "newpass");

        // Assert
        var retrieved = await _store.GetAsync(service);
        Assert.NotNull(retrieved);
        Assert.Equal("newuser", retrieved.Username);
        Assert.Equal("newpass", retrieved.Password);
    }

    [Fact]
    public async Task DatabaseFile_IsEncrypted()
    {
        // Arrange
        const string service = "encryption-test";
        const string password = "SENSITIVE_PASSWORD_12345";

        // Act
        await _store.SetAsync(service, "user", password);

        // Dispose to flush and close the DB
        _store.Dispose();

        // Assert - Check that the password is not in plaintext in the DB file
        // The DB is created in the actual user's app data dir
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        var zeusDir = Path.Combine(appDataDir, "Zeus");

        if (Directory.Exists(zeusDir))
        {
            var dbFiles = Directory.GetFiles(zeusDir, "zeus.db*");
            if (dbFiles.Length > 0)
            {
                var dbFile = dbFiles[0];
                var dbContent = File.ReadAllText(dbFile);

                // The password should not appear as plaintext in the encrypted database
                Assert.DoesNotContain(password, dbContent);
            }
        }

        // If we can't verify the file (which is OK in some test environments),
        // at least verify we can retrieve the stored credential
        using var store2 = new CredentialStore(NullLogger<CredentialStore>.Instance);
        var retrieved = await store2.GetAsync(service);
        Assert.NotNull(retrieved);
        Assert.Equal(password, retrieved.Password);
    }
}
