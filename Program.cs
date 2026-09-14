using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=accounts.db"));
builder.Services.AddCors(o => o.AddPolicy("all", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors("all");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();

    if (!await db.Servers.AnyAsync())
    {
        db.Servers.AddRange(
            new Server { Name = "General Chat", Description = "Main text chat server" },
            new Server { Name = "Gaming Chat", Description = "Talk about games" },
            new Server { Name = "Test Server", Description = "Testing server" });
        await db.SaveChangesAsync();
    }
}

var sessions = new ConcurrentDictionary<string, string>();
var members = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();

app.MapGet("/", () => Results.Ok(new { name = "TextChat Server", status = "online" }));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/servers", async (AppDb db) =>
{
    var servers = await db.Servers.OrderBy(x => x.Id).ToListAsync();

    var result = servers.Select(x => new
    {
        x.Id,
        x.Name,
        x.Description,
        PlayerCount = members.TryGetValue(x.Id, out var serverMembers)
            ? serverMembers.Count
            : 0
    }).ToList();

    return Results.Ok(result);
});

app.MapPost("/api/register", async (RegisterRequest req, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    if (req.Username.Length < 3 || req.Username.Length > 24)
        return Results.BadRequest(new { error = "Username must be 3-24 characters." });

    if (req.Password.Length < 4)
        return Results.BadRequest(new { error = "Password must be at least 4 characters." });

    if (await db.Users.AnyAsync(x => x.Username == req.Username))
        return Results.Conflict(new { error = "Username already exists." });

    db.Users.Add(new User
    {
        Username = req.Username,
        PasswordHash = global::BCrypt.Net.BCrypt.HashPassword(req.Password),
        IsAdmin = false
    });

    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Account created." });
});

app.MapPost("/api/login", async (LoginRequest req, AppDb db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(x => x.Username == req.Username);

    if (user == null || !global::BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        return Results.Unauthorized();

    var token = Guid.NewGuid().ToString("N");
    sessions[token] = user.Username;

    return Results.Ok(new
    {
        token,
        username = user.Username,
        isAdmin = user.IsAdmin
    });
});

app.MapPost("/api/logout", (AuthRequest req) =>
{
    if (!string.IsNullOrWhiteSpace(req.Token))
        sessions.TryRemove(req.Token, out _);

    return Results.Ok();
});

app.MapPost("/api/servers/{id:int}/join", async (int id, AuthRequest req, AppDb db) =>
{
    if (!TryGetUser(req.Token, out var username))
        return Results.Unauthorized();

    if (!await db.Servers.AnyAsync(x => x.Id == id))
        return Results.NotFound(new { error = "Server not found." });

    var serverMembers = members.GetOrAdd(id, _ => new ConcurrentDictionary<string, byte>());
    serverMembers[username!] = 0;

    return Results.Ok(new { message = "Joined server." });
});

app.MapPost("/api/servers/{id:int}/leave", (int id, AuthRequest req) =>
{
    if (!TryGetUser(req.Token, out var username))
        return Results.Unauthorized();

    if (members.TryGetValue(id, out var serverMembers))
        serverMembers.TryRemove(username!, out _);

    return Results.Ok();
});

app.MapGet("/api/servers/{id:int}/messages", async (int id, string token, AppDb db) =>
{
    if (!TryGetUser(token, out _))
        return Results.Unauthorized();

    var messages = await db.Messages
        .Where(x => x.ServerId == id)
        .OrderByDescending(x => x.Id)
        .Take(100)
        .OrderBy(x => x.Id)
        .Select(x => new
        {
            x.Id,
            x.Username,
            x.Text,
            x.CreatedAt
        })
        .ToListAsync();

    return Results.Ok(messages);
});

app.MapPost("/api/servers/{id:int}/messages", async (int id, SendMessageRequest req, AppDb db) =>
{
    if (!TryGetUser(req.Token, out var username))
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "Message cannot be empty." });

    if (!await db.Servers.AnyAsync(x => x.Id == id))
        return Results.NotFound(new { error = "Server not found." });

    var message = new ChatMessage
    {
        ServerId = id,
        Username = username!,
        Text = req.Text.Trim(),
        CreatedAt = DateTime.UtcNow
    };

    db.Messages.Add(message);
    await db.SaveChangesAsync();

    return Results.Ok(message);
});

app.MapGet("/api/servers/{id:int}/members", (int id) =>
{
    if (!members.TryGetValue(id, out var serverMembers))
        return Results.Ok(Array.Empty<string>());

    return Results.Ok(serverMembers.Keys.OrderBy(x => x).ToArray());
});

app.MapGet("/api/me", async (string token, AppDb db) =>
{
    if (!TryGetUser(token, out var username))
        return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(x => x.Username == username);

    if (user == null)
        return Results.Unauthorized();

    return Results.Ok(new { user.Username, user.IsAdmin });
});

app.MapPost("/api/admin/kick", async (AdminActionRequest req, AppDb db) =>
{
    if (!await IsAdmin(req.Token, db))
        return Results.Forbid();

    if (members.TryGetValue(req.ServerId, out var serverMembers))
        serverMembers.TryRemove(req.Username, out _);

    return Results.Ok(new { message = "User kicked." });
});

app.MapPost("/api/admin/ban", async (BanRequest req, AppDb db) =>
{
    if (!await IsAdmin(req.Token, db))
        return Results.Forbid();

    if (!await db.Users.AnyAsync(x => x.Username == req.Username))
        return Results.NotFound(new { error = "User not found." });

    if (!await db.Bans.AnyAsync(x => x.ServerId == req.ServerId && x.Username == req.Username))
    {
        db.Bans.Add(new Ban
        {
            ServerId = req.ServerId,
            Username = req.Username
        });

        await db.SaveChangesAsync();
    }

    if (members.TryGetValue(req.ServerId, out var serverMembers))
        serverMembers.TryRemove(req.Username, out _);

    return Results.Ok(new { message = "User banned from server." });
});

bool TryGetUser(string? token, out string? username)
{
    username = null;

    if (string.IsNullOrWhiteSpace(token))
        return false;

    return sessions.TryGetValue(token, out username);
}

async Task<bool> IsAdmin(string? token, AppDb db)
{
    if (!TryGetUser(token, out var username))
        return false;

    return await db.Users.AnyAsync(x => x.Username == username && x.IsAdmin);
}

var adminUsername = Environment.GetEnvironmentVariable("ACCOUNT_ADMIN_USER");
var adminPassword = Environment.GetEnvironmentVariable("ACCOUNT_ADMIN_PASSWORD");

if (!string.IsNullOrWhiteSpace(adminUsername) && !string.IsNullOrWhiteSpace(adminPassword))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();

    var existing = await db.Users.FirstOrDefaultAsync(x => x.Username == adminUsername);

    if (existing == null)
    {
        db.Users.Add(new User
        {
            Username = adminUsername,
            PasswordHash = global::BCrypt.Net.BCrypt.HashPassword(adminPassword),
            IsAdmin = true
        });

        await db.SaveChangesAsync();
    }
    else if (!existing.IsAdmin)
    {
        existing.IsAdmin = true;
        await db.SaveChangesAsync();
    }
}

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Run($"http://0.0.0.0:{port}");

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record AuthRequest(string Token);
public record SendMessageRequest(string Token, string Text);
public record AdminActionRequest(string Token, int ServerId, string Username);
public record BanRequest(string Token, int ServerId, string Username);

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<Ban> Bans => Set<Ban>();
}

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
}

public class Server
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public class ChatMessage
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Username { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class Ban
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Username { get; set; } = "";
}
