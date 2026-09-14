# TextChat Server

ASP.NET Core 8 text-chat backend for a WinForms client.

Features:
- Registration and login
- BCrypt password hashing
- Server browser
- Multiple chat servers
- Join/leave
- Chat history
- Online member lists
- Admin kick and per-server bans
- Render/Docker support
- `/health` endpoint

## Render
Use a Render Web Service with Docker and the Free plan.
Set:
- `ACCOUNT_ADMIN_USER`
- `ACCOUNT_ADMIN_PASSWORD`

The server automatically uses Render's `PORT`.

## WinForms client
Set the client's HTTP base address to:

`https://textchatfixed.onrender.com`

Example:
`new HttpClient { BaseAddress = new Uri("https://textchatfixed.onrender.com") };`

## Storage note
SQLite on a free Render service is not persistent. Accounts/messages may reset after a restart, redeploy, or spin-down. Use a persistent database for a permanent service.
