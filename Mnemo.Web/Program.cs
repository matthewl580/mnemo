using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Mnemo.Core.Models;
using Mnemo.Web.Models;
using Mnemo.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var jwtSecret = builder.Configuration["SyncServer:JwtSecret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    jwtSecret = "ReplaceThisSecretWithAnEnvironmentSecret";
}
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();
await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapPost("/auth/register", async (RegisterRequest request, UserService userService) =>
{
    if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new { Error = "UserName, Email, and Password are required." });
    }

    var registration = await userService.RegisterAsync(request.UserName.Trim(), request.Email.Trim(), request.Password).ConfigureAwait(false);
    if (!registration.IsSuccess)
    {
        return Results.BadRequest(new { Error = registration.ErrorMessage });
    }

    return Results.Created($"/auth/users/{registration.Value!.UserId}", new AuthResponse("", registration.Value.UserName, registration.Value.Email));
});

app.MapPost("/auth/login", async (LoginRequest request, UserService userService) =>
{
    if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { Error = "UserName and Password are required." });
    }

    var auth = await userService.AuthenticateAsync(request.UserName.Trim(), request.Password).ConfigureAwait(false);
    if (!auth.IsSuccess)
    {
        return Results.Unauthorized();
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, auth.Value!.UserId),
        new Claim(ClaimTypes.Name, auth.Value.UserName),
        new Claim(ClaimTypes.Email, auth.Value.Email)
    };

    var token = new JwtSecurityToken(
        claims: claims,
        expires: DateTime.UtcNow.AddDays(7),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

    return Results.Ok(new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), auth.Value.UserName, auth.Value.Email));
});

app.MapGet("/sync/notes", async (ClaimsPrincipal user, SyncService syncService) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var notes = await syncService.GetNotesAsync(userId).ConfigureAwait(false);
    return Results.Ok(notes);
}).RequireAuthorization();

app.MapGet("/sync/notes/{noteId}", async (ClaimsPrincipal user, string noteId, SyncService syncService) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var note = await syncService.GetNoteAsync(userId, noteId).ConfigureAwait(false);
    return note is not null ? Results.Ok(note) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/sync/notes", async (ClaimsPrincipal user, Note note, SyncService syncService) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var result = await syncService.SaveNoteAsync(userId, note).ConfigureAwait(false);
    return result.IsSuccess ? Results.Ok(note) : Results.Problem(result.ErrorMessage);
}).RequireAuthorization();

app.MapDelete("/sync/notes/{noteId}", async (ClaimsPrincipal user, string noteId, SyncService syncService) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var result = await syncService.DeleteNoteAsync(userId, noteId).ConfigureAwait(false);
    return result.IsSuccess ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

app.Run();
