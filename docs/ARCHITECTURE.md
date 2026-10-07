# Architecture

A short map of how SmartBank is put together. For the reasoning behind individual decisions see
[`DEFENSE.md`](DEFENSE.md) (in Turkish, with an English summary at the top); for configuration see the README.

## Layers

```
src/
  SmartBank.Core            entities, DTOs, interfaces, validators and the pure security rules
                            (one-time codes, login lockout, card masking, encryption helper, T.C. Kimlik No check)
  SmartBank.Infrastructure  EF Core DbContext and migrations, services (banking, auth, chat, market rates, e-mail),
                            the standing-order background worker, JWT settings
  SmartBank.API             ASP.NET Core host: controllers, SignalR hub, middleware, rate limiting, CORS, health checks
  SmartBank.Web             static frontend (HTML, CSS, vanilla JavaScript), published to GitHub Pages
  SmartBank.Tests           xUnit: unit, real-database and whole-application integration tests
```

Dependencies point inwards: `API -> Infrastructure -> Core`; the tests reference all three. `Core` has no dependency on
EF Core or ASP.NET, which keeps the security rules testable without a database or a web server.

## Deployment topology

```mermaid
flowchart LR
    User["Browser"] -->|"static files"| Pages["GitHub Pages<br/>(gh-pages branch)"]
    User -->|"HTTPS + JWT, WebSocket (SignalR)"| API
    subgraph Render["Render (free tier, Docker)"]
        API["SmartBank.API<br/>container, port 8080<br/>health check: /health"]
    end
    API -->|"Npgsql, session pooler"| DB[("Supabase PostgreSQL")]
    API -->|"HTTPS API"| Brevo["Brevo<br/>one-time-code e-mails"]
    API -.->|"HTTPS"| Gemini["Gemini API<br/>AI chat (when Ollama is down)"]
    API -.->|"HTTPS"| Rates["Third-party rates feed<br/>(simulated fallback)"]
    API -.->|"localhost only"| Ollama["Ollama<br/>(local development)"]
```

- The frontend is published with `scripts/deploy-pages.ps1` (or `.sh`) from `src/SmartBank.Web`; it is not served by the API.
- The API image is built from the `Dockerfile` (non-root, port 8080, forwarded headers on because Render terminates TLS).
- The production schema is **not** created by the application (it never calls `MigrateAsync`; with hand-made tables behind
  Supabase's connection pooler that would not be safe). It is created with `docs/deploy/00-baseline-postgres.sql` and upgraded with the numbered scripts in
  `docs/deploy`. `docs/deploy/schema-check.sql` lists the production columns for comparison with the model.
- Local development uses SQL Server LocalDB (the EF migrations target SQL Server) or the PostgreSQL container from
  `docker-compose.yml`. CI runs the real-database tests on PostgreSQL 16 and SQL Server 2022.

## Request flow

Every HTTP request passes through the same pipeline (`Program.cs`):

1. `GlobalExceptionMiddleware`: unhandled errors become a problem-details body with a trace id and no internals.
2. `SecurityHeadersMiddleware`: `nosniff`, `X-Frame-Options`, CSP `default-src 'none'`, `no-referrer`, `no-store` on `/api` and `/hubs`.
3. HSTS (production only), HTTPS redirection.
4. CORS with an explicit origin allow-list.
5. Authentication (JWT bearer): who is calling.
6. Rate limiting, after authentication on purpose so that per-user policies can see the user: per client IP for auth,
   refresh and market rates, per signed-in user (IP when anonymous) for the banking endpoints, and a stricter per-user
   policy for money-moving calls.
7. Authorization (roles `Customer` and `Agent`).
8. Controller or SignalR hub, which calls a service in `Infrastructure`.

Behind Render's proxy the client address comes from the forwarded headers (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in the
image, applied before the pipeline above); without it every client would share one rate-limit bucket.

Money movements (transfer, deposit, exchange, card payment and charge, account closing) go through one retry wrapper
in `BankingService`: every update is `WHERE Id = @id AND Version = @read`, and a conflict repeats the operation from fresh
reads. The standing-order worker (`StandingOrderExecutionWorker`) wakes every 30 seconds and executes due orders in
separate scopes and transactions.

## Authentication and refresh tokens

```mermaid
sequenceDiagram
    participant B as Browser
    participant A as API
    participant D as Database
    B->>A: POST /api/auth/login (T.C. no + PIN)
    A->>D: check PIN, lockout, 2FA setting
    alt 2FA on
        A-->>B: code e-mailed (shown only with Demo__ExposeOtp)
        B->>A: POST /api/auth/verify-2fa
    end
    A->>D: store SHA-256 hash of a new refresh token (new family)
    A-->>B: access token (15 min) + refresh token (single use)
    Note over B,A: ... access token about to expire ...
    B->>A: POST /api/auth/refresh (refresh token)
    A->>D: mark old token used, store hash of the new one (same family)
    A-->>B: new access token + new refresh token
    Note over A,D: a used refresh token presented again revokes the whole family
    B->>A: POST /api/auth/logout
    A->>D: revoke the family
```

Roles live in the database and in the token; an `Agent` can only be created by an administrator with SQL. Logout revokes the
one session (family) it belongs to; a password reset revokes all of the user's sessions. Failed sign-ins and the account
lockout do **not** revoke sessions (v1.3): otherwise anyone who knew a T.C. number could keep signing its owner out. The
SignalR connection takes the access token through `accessTokenFactory` (query string
`access_token`, accepted only on `/hubs`).

## Support chat

The browser connects to the SignalR hub (`/hubs/support`). A customer message is stored, then answered by the AI bot:
FAQ retrieval (`RAGService`) feeds Ollama when it is reachable, otherwise Gemini. The model can only propose actions
(balance lookup, a transfer the customer must confirm); machine markers typed by users or produced by the model are made
inert on the server, and per-user size and rate limits apply to every hub method. An agent can take over a conversation.

## Where to look

| Question | Place |
|---|---|
| How do I run it, configure it, deploy it? | [`../README.md`](../README.md) |
| What changed in each release? | [`../CHANGELOG.md`](../CHANGELOG.md) |
| Why was it done this way? | [`DEFENSE.md`](DEFENSE.md) |
| Database scripts | [`deploy/`](deploy) |
