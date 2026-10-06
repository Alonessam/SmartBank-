# Documentation index

| Document | What it is | Language |
|---|---|---|
| [`../README.md`](../README.md) / [`../README.tr.md`](../README.tr.md) | Overview, quick start, configuration, security model, known limitations | English / Turkish |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Layers, request pipeline, auth and refresh-token flow, deployment topology | English |
| [`DEFENSE.md`](DEFENSE.md) | Engineering notes (T1-T15): problem, change, reasoning, rejected alternatives, limits | Turkish (English summary at the top) |
| [`../CHANGELOG.md`](../CHANGELOG.md) | Changes per release, with upgrade checklists | English |
| [`../SECURITY.md`](../SECURITY.md) | How to report a vulnerability | English |
| [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | How to build, test and propose a change | English |

## Database scripts (`deploy/`)

| Script | Use it for |
|---|---|
| [`deploy/00-baseline-postgres.sql`](deploy/00-baseline-postgres.sql) | Creating the tables in an **empty** PostgreSQL database (fresh install). Generated from the EF model. |
| [`deploy/v1.1-postgres-upgrade.sql`](deploy/v1.1-postgres-upgrade.sql) | Upgrading an existing production database from v1.0 to v1.1 (card hardening, lockout, versions, roles). |
| [`deploy/v1.2-postgres-upgrade.sql`](deploy/v1.2-postgres-upgrade.sql) | v1.1 to v1.2: refresh tokens and schema fixes. |
| [`deploy/v1.3-postgres-upgrade.sql`](deploy/v1.3-postgres-upgrade.sql) | v1.2 to v1.3. |
| [`deploy/schema-check.sql`](deploy/schema-check.sql) | Listing the production columns to compare with what the code expects. |

Apply the upgrade scripts **in order**, once each, before deploying the matching API version. Never run them on a database
created from the baseline: it already contains their changes.
