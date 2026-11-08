using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.AccountServer;

public sealed class AccountDatabase
{
    private const int PasswordIterations = 600_000;
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly byte[] _dummySalt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _dummyHash = RandomNumberGenerator.GetBytes(32);
    public AccountRegion Region { get; }

    public AccountDatabase(string directory, TimeProvider clock, AccountRegion region = AccountRegion.China)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "accounts.db"), ForeignKeys = true, DefaultTimeout = 30 }.ToString();
        _clock = clock;
        Region = region;
    }

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync(CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Accounts (
                Id TEXT PRIMARY KEY, Username TEXT NOT NULL, NormalizedUsername TEXT NOT NULL UNIQUE,
                Salt BLOB NOT NULL, PasswordHash BLOB NOT NULL, CreatedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS AccountSessions (
                TokenHash BLOB PRIMARY KEY, AccountId TEXT NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_AccountSessions_Expiry ON AccountSessions(ExpiresAt);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<AccountSessionResponse> RegisterAsync(AccountCredentials credentials, CancellationToken token)
    {
        var username = Validate(credentials);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = await Task.Run(() => PasswordHash(credentials.Password, salt), token);
        var user = new AccountUser(Guid.NewGuid(), username);
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Accounts VALUES ($id, $username, $normalized, $salt, $hash, $created);";
        command.Parameters.AddWithValue("$id", user.Id.ToString());
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$normalized", username.ToLowerInvariant());
        command.Parameters.AddWithValue("$salt", salt);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$created", _clock.GetUtcNow().ToUnixTimeSeconds());
        try { await command.ExecuteNonQueryAsync(token); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        { throw new AccountRequestException(StatusCodes.Status409Conflict, "该用户名已注册。"); }
        var session = await CreateSessionAsync(connection, transaction, user, token);
        await transaction.CommitAsync(token);
        return session;
    }

    public async Task<AccountSessionResponse> SignInAsync(AccountCredentials credentials, CancellationToken token)
    {
        var username = Validate(credentials);
        await using var connection = await OpenAsync(token);
        AccountUser? user = null;
        var salt = _dummySalt;
        var expected = _dummyHash;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Username, Salt, PasswordHash FROM Accounts WHERE NormalizedUsername = $username;";
            command.Parameters.AddWithValue("$username", username.ToLowerInvariant());
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                user = new(Guid.Parse(reader.GetString(0)), reader.GetString(1));
                salt = (byte[])reader[2]; expected = (byte[])reader[3];
            }
        }
        var actual = await Task.Run(() => PasswordHash(credentials.Password, salt), token);
        var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
        CryptographicOperations.ZeroMemory(actual);
        if (!valid || user is null)
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "用户名或密码错误。");
        return await CreateSessionAsync(connection, null, user, token);
    }

    public async Task<AccountSessionInfo> GetSessionAsync(string bearer, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id, a.Username, s.ExpiresAt FROM AccountSessions s
            JOIN Accounts a ON a.Id = s.AccountId WHERE s.TokenHash = $hash AND s.ExpiresAt > $now;
            """;
        command.Parameters.AddWithValue("$hash", TokenHash(bearer));
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeSeconds());
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "会话已失效，请重新登录。");
        return new(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)), Region);
    }

    public async Task RevokeAsync(string bearer, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AccountSessions WHERE TokenHash = $hash;";
        command.Parameters.AddWithValue("$hash", TokenHash(bearer));
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<AccountSessionResponse> CreateSessionAsync(SqliteConnection connection, SqliteTransaction? transaction,
        AccountUser user, CancellationToken token)
    {
        var bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds());
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM AccountSessions WHERE ExpiresAt <= $now;
            INSERT INTO AccountSessions VALUES ($hash, $account, $expires);
            """;
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$hash", TokenHash(bearer));
        command.Parameters.AddWithValue("$account", user.Id.ToString());
        command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(token);
        return new() { User = user, Token = bearer, ExpiresAt = expiresAt, Region = Region };
    }

    private string Validate(AccountCredentials credentials)
    {
        if (credentials.Region != Region) throw new AccountRequestException(StatusCodes.Status403Forbidden, "账号服务区域不匹配。");
        var username = credentials.Username?.Trim() ?? "";
        if (!AccountValidation.IsUsername(username) || !AccountValidation.IsPassword(credentials.Password))
            throw new AccountRequestException(StatusCodes.Status400BadRequest, "用户名或密码格式无效。");
        return username;
    }

    private static byte[] PasswordHash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
    private static byte[] TokenHash(string bearer) => SHA256.HashData(Encoding.ASCII.GetBytes(bearer));

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
