using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=accounts.db"));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    if (!await db.Servers.AnyAsync())
    {
        db.Servers.AddRange(
            new ChatServer { Name = "General Chat", Description = "The main chat room", MaxPlayers = 50 },
            new ChatServer { Name = "Gaming Chat", Description = "Talk about games", MaxPlayers = 20 },
            new ChatServer { Name = "Test Server", Description = "A small test room", MaxPlayers = 10 });
        await db.SaveChangesAsync();
    }

    // Optional first admin. Change these environment variables before starting the server.
    var adminUser = Environment.GetEnvironmentVariable("ACCOUNT_ADMIN_USER");
    var adminPass = Environment.GetEnvironmentVariable("ACCOUNT_ADMIN_PASSWORD");
    if (!string.IsNullOrWhiteSpace(adminUser) && !string.IsNullOrWhiteSpace(adminPass) &&
        !await db.Users.AnyAsync(x => x.Username.ToLower() == adminUser.ToLower()))
    {
        db.Users.Add(new User { Username = adminUser.Trim(), PasswordHash = global::BCrypt.Net.BCrypt.HashPassword(adminPass), Role = "Admin", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Console.WriteLine($"Created admin account: {adminUser}");
    }
}

var sessions = new ConcurrentDictionary<string, Session>();
var members = new ConcurrentDictionary<int, ConcurrentDictionary<string, string>>();

app.MapGet("/api/servers", async (AppDb db) =>
{
    var servers = await db.Servers.OrderBy(x => x.Id).ToListAsync();
    return Results.Ok(servers.Select(s => new { s.Id, s.Name, s.Description, s.MaxPlayers, Players = members.TryGetValue(s.Id, out var m) ? m.Count : 0 }));
});

app.MapPost("/api/register", async (RegisterRequest req, AppDb db) =>
{
    var username = req.Username?.Trim() ?? "";
    var password = req.Password ?? "";
    if (username.Length < 3 || username.Length > 32) return Results.BadRequest(new { message = "Username must be 3-32 characters." });
    if (!username.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')) return Results.BadRequest(new { message = "Username can use letters, numbers, _ and -." });
    if (password.Length < 8 || password.Length > 128) return Results.BadRequest(new { message = "Password must be 8-128 characters." });
    if (await db.Users.AnyAsync(x => x.Username.ToLower() == username.ToLower())) return Results.Conflict(new { message = "Username already exists." });
    db.Users.Add(new User { Username = username, PasswordHash = global::BCrypt.Net.BCrypt.HashPassword(password), Role = "User", CreatedUtc = DateTime.UtcNow });
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Account created." });
});

app.MapPost("/api/login", async (LoginRequest req, AppDb db) =>
{
    var username = req.Username?.Trim() ?? "";
    var user = await db.Users.FirstOrDefaultAsync(x => x.Username.ToLower() == username.ToLower());
    if (user is null || !global::BCrypt.Net.BCrypt.Verify(req.Password ?? "", user.PasswordHash)) return Results.Unauthorized();
    if (await db.Bans.AnyAsync(x => x.Username.ToLower() == user.Username.ToLower() && (x.ExpiresUtc == null || x.ExpiresUtc > DateTime.UtcNow))) return Results.StatusCode(403);
    var token = Guid.NewGuid().ToString("N");
    sessions[token] = new Session(user.Id, user.Username, user.Role);
    return Results.Ok(new { username = user.Username, role = user.Role, token });
});

app.MapPost("/api/logout", (HttpRequest request) =>
{
    sessions.TryRemove(GetToken(request), out _);
    return Results.Ok();
});

app.MapPost("/api/servers/{serverId:int}/join", async (int serverId, HttpRequest request, AppDb db) =>
{
    if (!TrySession(request, sessions, out var session)) return Results.Unauthorized();
    if (await db.Bans.AnyAsync(x => x.Username.ToLower() == session.Username.ToLower() && x.ServerId == serverId && (x.ExpiresUtc == null || x.ExpiresUtc > DateTime.UtcNow))) return Results.StatusCode(403);
    var server = await db.Servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    var room = members.GetOrAdd(serverId, _ => new ConcurrentDictionary<string, string>());
    if (room.Count >= server.MaxPlayers && !room.ContainsKey(session.Username)) return Results.Conflict(new { message = "Server is full." });
    room[session.Username] = session.Username;
    return Results.Ok(new { message = $"Joined {server.Name}." });
});

app.MapPost("/api/servers/{serverId:int}/leave", (int serverId, HttpRequest request) =>
{
    if (!TrySession(request, sessions, out var session)) return Results.Unauthorized();
    if (members.TryGetValue(serverId, out var room)) room.TryRemove(session.Username, out _);
    return Results.Ok();
});

app.MapGet("/api/servers/{serverId:int}/messages", async (int serverId, int? afterId, HttpRequest request, AppDb db) =>
{
    if (!TrySession(request, sessions, out _)) return Results.Unauthorized();
    var query = db.Messages.Where(x => x.ServerId == serverId).OrderBy(x => x.Id);
    if (afterId.HasValue) query = (IOrderedQueryable<ChatMessage>)query.Where(x => x.Id > afterId.Value);
    var messages = await query.Take(100).ToListAsync();
    return Results.Ok(messages.Select(x => new { x.Id, x.Username, x.Text, x.CreatedUtc }));
});

app.MapPost("/api/servers/{serverId:int}/messages", async (int serverId, SendMessageRequest req, HttpRequest request, AppDb db) =>
{
    if (!TrySession(request, sessions, out var session)) return Results.Unauthorized();
    if (!members.TryGetValue(serverId, out var room) || !room.ContainsKey(session.Username)) return Results.BadRequest(new { message = "Join the server first." });
    var text = (req.Text ?? "").Trim();
    if (text.Length == 0 || text.Length > 500) return Results.BadRequest(new { message = "Message must be 1-500 characters." });
    db.Messages.Add(new ChatMessage { ServerId = serverId, Username = session.Username, Text = text, CreatedUtc = DateTime.UtcNow });
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/servers/{serverId:int}/members", (int serverId, HttpRequest request) =>
{
    if (!TrySession(request, sessions, out _)) return Results.Unauthorized();
    var list = members.TryGetValue(serverId, out var room) ? room.Keys.OrderBy(x => x).ToArray() : Array.Empty<string>();
    return Results.Ok(list);
});

app.MapPost("/api/admin/kick", async (KickRequest req, HttpRequest request, AppDb db) =>
{
    if (!TrySession(request, sessions, out var admin) || admin.Role != "Admin") return Results.StatusCode(403);
    if (members.TryGetValue(req.ServerId, out var room)) room.TryRemove(req.Username, out _);
    var target = sessions.FirstOrDefault(x => x.Value.Username.Equals(req.Username, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrEmpty(target.Key)) sessions.TryRemove(target.Key, out _);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "User kicked." });
});

app.MapPost("/api/admin/ban", async (BanRequest req, HttpRequest request, AppDb db) =>
{
    if (!TrySession(request, sessions, out var admin) || admin.Role != "Admin") return Results.StatusCode(403);
    var username = req.Username.Trim();
    if (string.IsNullOrWhiteSpace(username)) return Results.BadRequest(new { message = "Username required." });
    db.Bans.Add(new Ban { Username = username, ServerId = req.ServerId, Reason = string.IsNullOrWhiteSpace(req.Reason) ? "Banned by admin" : req.Reason.Trim(), ExpiresUtc = null, CreatedUtc = DateTime.UtcNow });
    await db.SaveChangesAsync();
    if (members.TryGetValue(req.ServerId, out var room)) room.TryRemove(username, out _);
    foreach (var pair in sessions.Where(x => x.Value.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).ToList()) sessions.TryRemove(pair.Key, out _);
    return Results.Ok(new { message = "User banned from this server." });
});

app.MapGet("/api/me", (HttpRequest request) =>
{
    if (!TrySession(request, sessions, out var s)) return Results.Unauthorized();
    return Results.Ok(new { username = s.Username, role = s.Role });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Run($"http://0.0.0.0:{port}");

static string GetToken(HttpRequest request) => request.Headers["X-Session-Token"].FirstOrDefault() ?? "";
static bool TrySession(HttpRequest request, ConcurrentDictionary<string, Session> sessions, out Session session) => sessions.TryGetValue(GetToken(request), out session!);
record RegisterRequest(string? Username, string? Password);
record LoginRequest(string? Username, string? Password);
record SendMessageRequest(string? Text);
record KickRequest(int ServerId, string Username);
record BanRequest(int ServerId, string Username, string? Reason);
record Session(int UserId, string Username, string Role);
class User { public int Id { get; set; } public string Username { get; set; } = ""; public string PasswordHash { get; set; } = ""; public string Role { get; set; } = "User"; public DateTime CreatedUtc { get; set; } }
class ChatServer { public int Id { get; set; } public string Name { get; set; } = ""; public string Description { get; set; } = ""; public int MaxPlayers { get; set; } }
class ChatMessage { public int Id { get; set; } public int ServerId { get; set; } public string Username { get; set; } = ""; public string Text { get; set; } = ""; public DateTime CreatedUtc { get; set; } }
class Ban { public int Id { get; set; } public string Username { get; set; } = ""; public int ServerId { get; set; } public string Reason { get; set; } = ""; public DateTime? ExpiresUtc { get; set; } public DateTime CreatedUtc { get; set; } }
class AppDb : DbContext { public AppDb(DbContextOptions<AppDb> options) : base(options) { } public DbSet<User> Users => Set<User>(); public DbSet<ChatServer> Servers => Set<ChatServer>(); public DbSet<ChatMessage> Messages => Set<ChatMessage>(); public DbSet<Ban> Bans => Set<Ban>(); }
