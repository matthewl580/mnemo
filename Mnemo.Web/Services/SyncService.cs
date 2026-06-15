using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mnemo.Core.Models;

namespace Mnemo.Web.Services;

public sealed class SyncService
{
    private readonly IConfiguration _configuration;

    public SyncService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<IEnumerable<Note>> GetNotesAsync(string userId)
    {
        var dbFile = _configuration["SyncServer:DbFile"];
        await using var connection = new SqliteConnection($"Data Source={dbFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT Json FROM Notes WHERE UserId = $userId ORDER BY ModifiedAt DESC;";
        command.Parameters.AddWithValue("$userId", userId);

        var notes = new List<Note>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var json = reader.GetString(0);
            var note = JsonSerializer.Deserialize<Note>(json);
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        return notes;
    }

    public async Task<Note?> GetNoteAsync(string userId, string noteId)
    {
        var dbFile = _configuration["SyncServer:DbFile"];
        await using var connection = new SqliteConnection($"Data Source={dbFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT Json FROM Notes WHERE UserId = $userId AND NoteId = $noteId;";
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$noteId", noteId);

        var result = await command.ExecuteScalarAsync();
        if (result is string json)
        {
            return JsonSerializer.Deserialize<Note>(json);
        }

        return null;
    }

    public async Task<Result> SaveNoteAsync(string userId, Note note)
    {
        note.ModifiedAt = DateTime.UtcNow;
        if (note.CreatedAt == default)
        {
            note.CreatedAt = note.ModifiedAt;
        }

        var dbFile = _configuration["SyncServer:DbFile"];
        await using var connection = new SqliteConnection($"Data Source={dbFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO Notes (NoteId, UserId, Json, CreatedAt, ModifiedAt)
            VALUES ($noteId, $userId, $json, $createdAt, $modifiedAt)
            ON CONFLICT(NoteId) DO UPDATE SET Json = $json, ModifiedAt = $modifiedAt;";
        command.Parameters.AddWithValue("$noteId", note.NoteId);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(note));
        command.Parameters.AddWithValue("$createdAt", note.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$modifiedAt", note.ModifiedAt.ToString("O"));

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            return Result.Failure("Failed to save note.", ex);
        }

        return Result.Success();
    }

    public async Task<Result> DeleteNoteAsync(string userId, string noteId)
    {
        var dbFile = _configuration["SyncServer:DbFile"];
        await using var connection = new SqliteConnection($"Data Source={dbFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"DELETE FROM Notes WHERE UserId = $userId AND NoteId = $noteId;";
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$noteId", noteId);

        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0 ? Result.Success() : Result.Failure("Note not found.");
    }
}
