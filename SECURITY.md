# Security Policy

SmartBank is a **portfolio / demo project**. The money, cards and exchange rates are simulated, and the public demo must
never hold real personal data. Security reports are still welcome: the project exists to show careful handling of them.

## Supported versions

Only the latest release (see [CHANGELOG.md](CHANGELOG.md)) receives fixes.

| Version | Supported |
|---|---|
| 1.3.x   | yes |
| < 1.3   | no  |

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

1. Go to the repository's **Security** tab and choose **Report a vulnerability** (GitHub private vulnerability reporting):
   <https://github.com/Alonessam/SmartBank-/security/advisories/new>
2. Describe what you found, how to reproduce it and what you think the impact is.

You can expect an acknowledgement within a few days. This is a solo hobby project, so there is no bounty and no SLA,
but confirmed issues are fixed with a test and credited in the changelog if you wish.

## Scope

In scope: the API (`src/SmartBank.API`, `src/SmartBank.Infrastructure`, `src/SmartBank.Core`), the static web app
(`src/SmartBank.Web`), the Dockerfile, the CI workflows and the SQL scripts in `docs/deploy`.

Out of scope: the free-tier hosting providers themselves (Render, Supabase, GitHub Pages), denial of service by volume
against the free demo, and anything that needs real personal data (there is none).

Known limitations that are already documented (so no report is needed) are listed in the README under
"Known limitations".

## Secrets in the repository

No live secret is in the repository. The git history of the first release (v1.0) contains the original JWT and AES keys; they
were rotated in v1.1 and must be treated as compromised (a secret scanner will find them in old commits, that is expected).
The throw-away credentials in `.github/workflows/ci.yml`, `docker-compose.yml` and the header of
`docs/deploy/00-baseline-postgres.sql` are labelled as such: they protect nothing but a container that exists for a few
minutes (CI) or on a developer's own machine (compose, bound to `127.0.0.1`).
