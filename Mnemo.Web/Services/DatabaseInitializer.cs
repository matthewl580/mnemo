using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Mnemo.Web.Models;

namespace Mnemo.Web.Services;

public sealed class DatabaseInitializer
{
    private readonly IConfiguration _configuration;

    public DatabaseInitializer(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task InitializeAsync()
    {
        var dbFile = _configuration["SyncServer:DbFile"];
        if (string.IsNullOrWhiteSpace(dbFile))
        {
            throw new InvalidOperationException("SyncServer:DbFile configuration is required.");
        }

        var directory = Path.GetDirectoryName(dbFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection($"Data Source={dbFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = @"CREATE TABLE IF NOT EXISTS Users (
            UserId TEXT PRIMARY KEY,
            UserName TEXT NOT NULL UNIQUE,
            Email TEXT NOT NULL UNIQUE,
            PasswordHash TEXT NOT NULL,
            PasswordSalt TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );";
        await command.ExecuteNonQueryAsync();

        command.CommandText = @"CREATE TABLE IF NOT EXISTS Notes (
            NoteId TEXT PRIMARY KEY,
            UserId TEXT NOT NULL,
            Json TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            ModifiedAt TEXT NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(UserId) ON DELETE CASCADE
        );";
        await command.ExecuteNonQueryAsync();
    }
}
