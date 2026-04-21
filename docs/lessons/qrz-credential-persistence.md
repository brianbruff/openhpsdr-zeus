# QRZ Credential Persistence

## Overview

QRZ credentials are now persisted locally to avoid re-entering passwords on every server restart. The implementation uses LiteDB (embedded document database) with encryption at rest.

## Storage Location

Credentials are stored in a LiteDB database in the user's application data directory:

- **Windows**: `%LOCALAPPDATA%\Zeus\zeus.db`
- **Linux**: `~/.local/share/Zeus/zeus.db`
- **macOS**: `~/.local/share/Zeus/zeus.db`

The database is **encrypted** using a per-install key stored in `.dbkey` in the same directory.

## Security Model

### Encryption at Rest
- Database is encrypted using LiteDB's built-in password protection
- Encryption key is randomly generated on first run (32 bytes, Base64-encoded)
- Key is stored in `.dbkey` with 0600 permissions on Unix-like systems

### Secrets Never Leaked
- **Logs**: Only username is logged, never password (see `QrzService.cs:38`, `QrzService.cs:280`)
- **HTTP Responses**: Only `HasStoredCredentials` flag is returned, never the password (see `QrzDtos.cs:27`)
- **Database**: Password is encrypted in the database file and not readable as plaintext

### Threat Model
This is **single-user desktop software** where the server runs on the operator's own machine. The threat model assumes:
- The server process is trusted (attacker with code execution can read memory anyway)
- The filesystem is trusted (attacker with filesystem access can read the operator's files anyway)
- Network transport is over localhost or LAN (already using HTTPS in production scenarios)

Encryption at rest protects against:
- Accidental commits to version control (.gitignore excludes `zeus.db*` and `.dbkey`)
- Casual inspection of backup archives
- Plaintext credential disclosure in filesystem forensics

## Lifecycle

### First Login
1. Operator enters username and password in browser
2. Frontend POSTs to `/api/qrz/login`
3. `QrzService.LoginAsync()` authenticates with QRZ XML API
4. On success, `CredentialStore.SetAsync()` persists credentials
5. Frontend sees `HasStoredCredentials: true` in response

### Server Restart
1. `QrzService.InitializeAsync()` runs on startup (see `Program.cs:73-74`)
2. If credentials exist in store, attempts silent login
3. On success, operator sees connected state immediately
4. On failure (e.g. password changed at QRZ), credentials are deleted and operator sees normal login prompt

### Logout
1. Operator clicks logout in browser
2. Frontend POSTs to `/api/qrz/logout`
3. `QrzService.LogoutAsync()` clears memory and deletes stored credentials
4. Next restart will not auto-login

### Credential Refresh
1. If stored credentials fail (e.g. password changed at QRZ), they're automatically deleted
2. Operator sees normal "not connected" state in UI
3. Operator re-enters credentials; new values overwrite the stored row

## Schema

```csharp
public sealed class StoredCredential
{
    public int Id { get; set; }
    public string Service { get; set; }    // "qrz", "rotctld", etc.
    public string Username { get; set; }
    public string Password { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
```

The `Service` field is indexed as unique, allowing future expansion for other credential types (rotctld, DX clusters, etc.) without schema changes.

## Testing

See `tests/Zeus.Server.Tests/CredentialStoreTests.cs` for unit tests covering:
- Basic CRUD operations
- Update/upsert behavior
- Encryption at rest verification
- Cross-instance persistence

## Load-Bearing Invariants

1. **Never log passwords**: Only log service name and username. See all `_log.LogInformation()` calls in `CredentialStore.cs` and `QrzService.cs`.
2. **Never return passwords in HTTP responses**: Only return `HasStoredCredentials` flag. See `QrzStatus` DTO.
3. **Database file is outside repo**: Stored in app-data dir, never in working directory. `.gitignore` includes `zeus.db*` and `.dbkey` as defence-in-depth.
4. **Silent login failures are graceful**: If stored credentials fail, they're deleted and the operator sees a normal login prompt (no error popups, no retry loops).

## Extending to Other Services

To add credential persistence for another service (e.g. rotctld):

1. Call `CredentialStore.SetAsync("rotctld", user, pass)` after successful connection
2. Call `CredentialStore.GetAsync("rotctld")` on startup to attempt silent reconnect
3. Call `CredentialStore.DeleteAsync("rotctld")` on explicit disconnect or credential failure
4. Add a `HasStoredCredentials` flag to the service's status DTO
5. Never log or return the password over HTTP

The `Service` discriminator allows multiple services to store credentials in the same database without collision.
