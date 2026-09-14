# Render-ready Text Chat Server

This folder contains the ASP.NET Core 8 server for the WinForms text-chat project, prepared for deployment on Render.

## Files

- `Program.cs` - API, accounts, chat rooms, sessions, members, admin kick/ban.
- `Server.csproj` - .NET 8 dependencies.
- `Dockerfile` - builds and runs the server on Render.
- `render.yaml` - Render Blueprint configuration.
- `.dockerignore` - keeps local/build files out of the Docker image.

## Render settings

If you connect this repository to Render, the Blueprint uses:

- Runtime: Docker
- Plan: Free
- Health check: `/health`
- Port: the `PORT` environment variable (Render normally provides `10000`)

Render gives the web service a public `onrender.com` URL after deployment.

## Admin account

Set these as Render environment variables (do NOT commit real passwords to GitHub):

- `ACCOUNT_ADMIN_USER`
- `ACCOUNT_ADMIN_PASSWORD`

The server creates that admin account on first startup if the username does not already exist.

## Important free-tier database note

This version still uses SQLite because it matches the current local project. Render's free web-service filesystem is ephemeral, so `accounts.db` can be lost when the free service spins down/restarts/redeploys. That means this is suitable for testing the public server, but it is **not yet a permanent account database**.

For a longer-lived version, move the database to PostgreSQL. Render currently offers a Free Postgres option, but its free database expires after 30 days, so that is also best treated as a testing option unless you later move to a paid/persistent database.
