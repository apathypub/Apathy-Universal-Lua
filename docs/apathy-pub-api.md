# apathy.pub client API

The desktop app logs in through three requests to apathy.pub. Only accounts with an active premium get
in. The app never talks to the database and never contains its password: the API on the site does.

A ready-made PHP version is in [`server/`](../server). It works with MySQL / MariaDB and any users table.

## Setting it up on the site

1. Copy `server/api/` into the site's web root, so `https://apathy.pub/api/client/login` reaches
   `api/client/index.php` (`.htaccess` handles the routing on Apache; on nginx use
   `rewrite ^/api/client/(login|session|logout)$ /api/client/index.php?action=$1 last;`).
2. Copy `server/config.example.php` to `config.php` **next to the `api` folder** (the web root) and
   fill in the database login and the names of your users table and its columns. If the site doesn't
   hash passwords with PHP's `password_hash()`, change `verify_password` to the site's own check.
   Block web access to it, e.g. on Apache:
   ```apache
   <Files config.php>
       Require all denied
   </Files>
   ```
3. Run `server/schema.sql` on the site's database. It adds two tables (`apathy_client_sessions`,
   `apathy_client_attempts`) and doesn't change the existing ones.
4. The login window links to `https://apathy.pub/register.php` and `https://apathy.pub/premium.php`.

To let users move to a new PC, give the site a button that clears their row(s) in
`apathy_client_devices` (`DELETE FROM apathy_client_devices WHERE user_id = ?`). The next login then
binds the new PC.

To try the app against a test server, start apathy with the environment variable
`APATHY_API_URL=http://localhost:8080`.

## Requests

All answers are JSON. Errors have an `error` field with a message the app shows as it is.

### `POST /api/client/login`

```json
{ "username": "name or e-mail", "password": "…", "hwid": "64 hex chars", "client_version": "1.0.0" }
```

`hwid` is a SHA-256 of the PC's Windows MachineGuid (the same PC always sends the same value). The
account **locks to the PCs it's used on** (up to `max_pcs`, default 1). A login from a different PC is
refused with `409` even when the password is right, so a shared login and password won't work on
someone else's machine. Free a PC slot with a reset on apathy.pub, or set `device_forget_days` so an
unused PC frees its slot by itself. `max_pcs = 0` turns the HWID lock off.

### `GET /api/client/session`

With the header `Authorization: Bearer <token>`. Checks a remembered login when the app starts, and
again every 15 minutes while it runs. Each check keeps the session alive for another `session_days`.

### `POST /api/client/logout`

With the same header. Ends the session.

## Answers

| Status | Meaning | What the app does |
| --- | --- | --- |
| `200` | `{ "username", "token", "premium": true, "premium_until": "2026-12-01T00:00:00Z" or null }` | Logs in. `null` means lifetime premium |
| `401` | Wrong login, or the token is unknown or expired | Asks for the password |
| `403` | The account has no active premium, or is banned | Shows the message and the "Get premium" link |
| `409` | The login is right, but this PC isn't one the account is locked to (HWID lock) | Shows the message; the user resets their PC on apathy.pub to move |
| `429` | Too many failed logins from this IP or for this name | Asks to wait |
| other / no answer | Server error or no internet | Login: shows the error. While running: keeps working and checks again later |

The token is random (32 bytes); the server stores only its SHA-256. When the app is running and a check
answers `401` or `403`, the user gets a notice and apathy closes.
