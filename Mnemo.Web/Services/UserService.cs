using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemo.Core.Models;
using Mnemo.Web.Models;

namespace Mnemo.Web.Services;

public sealed class UserService
{
    private readonly IConfiguration _configuration;

    public UserService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<Result<UserEntity>> RegisterAsync(string userName, string email, string password)
    {
        var user = new UserEntity
        {
            UserName = userName,
            Email = email,
        };

        CreatePasswordHash(password, out var hash, out var salt);
        user.PasswordHash = Convert.ToBase64String(hash);
        user.PasswordSalt = Convert.ToBase64String(salt);

        var dbFile = _configuration["SyncServer:DbFile"];
        await using var connection = new SqliteConnection($"Data Source={dbFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = @"INSERT INTO Users (UserId, UserName, Email, PasswordHash, PasswordSalt, CreatedAt)
            VALUES ($userId, $userName, $email, $passwordHash, $passwordSalt, $createdAt);";
        command.Parameters.AddWithValue("$userId", user.UserId);
        command.Parameters.AddWithValue("$userName", user.UserName);
        command.Parameters.AddWithValue("$email", user.Email);
        command.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
        command.Parameters.AddWithValue("$passwordSalt", user.PasswordSalt);
        command.Parameters.AddWithValue("$createdAt", user.CreatedAt.ToString("O"));

        try
        {
            await command.ExecuteNonQueryAsync();
            return Result<UserEntity>.Success(user);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Result<UserEntity>.Failure("A user with that username or email already exists.", ex);
        }
    }

    public async Task<Result<UserEntity>> AuthenticateAsync(string userName, string password)
    {
        var dbFile = _configuration["SyncServer:DbFile"];
        await using var connection = new SqliteConnection($"Data Source={dbFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = @"SELECT UserId, UserName, Email, PasswordHash, PasswordSalt FROM Users WHERE UserName = $userName;";
        command.Parameters.AddWithValue("$userName", userName);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return Result<UserEntity>.Failure("Invalid username or password.");
        }

        var storedHash = Convert.FromBase64String(reader.GetString(3));
        var storedSalt = Convert.FromBase64String(reader.GetString(4));

        if (!VerifyPasswordHash(password, storedHash, storedSalt))
        {
            return Result<UserEntity>.Failure("Invalid username or password.");
        }

        return Result<UserEntity>.Success(new UserEntity
        {
            UserId = reader.GetString(0),
            UserName = reader.GetString(1),
            Email = reader.GetString(2)
        });
    }

    private static void CreatePasswordHash(string password, out byte[] hash, out byte[] salt)
    {
        using var hmac = new HMACSHA512();
        salt = hmac.Key;
        hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
    }

    private static bool VerifyPasswordHash(string password, byte[] storedHash, byte[] storedSalt)
    {
        using var hmac = new HMACSHA512(storedSalt);
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
        return computedHash.SequenceEqual(storedHash);
    }
}
