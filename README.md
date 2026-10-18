<div align="center">

# 🏦 PADO BankCore
### Banking Transaction Simulator · ASP.NET Core · PostgreSQL

**A correctness-focused .NET 10 backend project showcasing transactional money movement, an auditable ledger, and cross-process concurrency control.**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-17-4169E1?logo=postgresql)
![EF Core](https://img.shields.io/badge/EF_Core-10-512BD4)
![License](https://img.shields.io/badge/license-MIT-blue)
[![CI](https://github.com/PADOALEX/PADO-BankCore/actions/workflows/ci.yml/badge.svg)](https://github.com/PADOALEX/PADO-BankCore/actions/workflows/ci.yml)

**Educational simulator only. No real money, banking integrations, regulatory certifications or production-grade financial claims.**

</div>

## Why this project?

The original `BankSystem.zip` was a C# console app with clients, checking/savings/investment accounts, transfers, transaction history and a multi-threaded stress test. It used sorted `lock` acquisition to avoid deadlocks *inside one process*. PADO BankCore preserves its account rules and replaces in-memory state with a database-backed implementation where correctness is tested against **real PostgreSQL**.

## Features

- **Three account types:** checking (standard), savings (three lifetime debits), investment (USD 1,000 minimum balance after debit).
- **Atomic posting:** changes to both accounts, the operation receipt and the signed ledger entries commit or roll back together.
- **Cross-process concurrency:** PostgreSQL `SELECT … FOR UPDATE` row locks acquired in deterministic account-ID order under `READ COMMITTED`.
- **Idempotency:** `(InitiatorId, IdempotencyKey)` unique database index; replays return the original operation ID; conflicting payloads yield HTTP 409.
- **Auditable journal:** one positive/negative signed entry for simulator deposits/withdrawals, exactly two balancing entries for transfers, plus balance-after snapshots.
- **JWT authentication:** password hashing with ASP.NET Core Identity PasswordHasher, owner-scoped account reads and posting permissions.
- **No hidden rounding:** decimal values mapped to `numeric(19,2)`; only `USD`, no FX, max transaction USD 1,000,000.
- **Developer tools:** .NET unit tests, PostgreSQL integration tests, Docker Compose, OpenAPI JSON, GitHub Actions and `.http` examples.

## Architecture

```text
PADO.BankCore.sln
├── src/
│   ├── BankCore.Domain/            Account rules, money validation, operation + ledger models
│   ├── BankCore.Infrastructure/    EF Core mappings, migrations, row-locking posting service, audits
│   └── BankCore.Api/               ASP.NET Core Minimal API, JWT, ownership checks, OpenAPI
├── tests/
│   ├── BankCore.UnitTests/         Isolated domain rule tests
│   └── BankCore.IntegrationTests/  Real PostgreSQL transactions and concurrency
├── examples/BankCore.http          End-to-end request examples
├── docs/TRANSACTIONAL_DESIGN.md   Invariants, failure modes, limitations
└── .github/workflows/ci.yml       Build, unit and PostgreSQL integration jobs
```

## Quick start — Docker

**Prerequisites:** Docker Engine and Docker Compose v2.

```bash
cp .env.example .env
# Edit .env: choose a unique PostgreSQL password and generate a random JWT signing key (>=32 bytes).
docker compose up --build -d
docker compose ps
curl http://localhost:8080/health
curl http://localhost:8080/openapi/v1.json
```

Local connections are bound to `127.0.0.1`. Postgres uses a named volume, so data persists across container restarts. `docker compose down -v` **deletes the local simulation database**.

### Quick start — without Docker

Install the **.NET 10 SDK** and **PostgreSQL 17+**, create a disposable database, then configure user secrets or environment variables:

```bash
export ConnectionStrings__BankCore='Host=localhost;Port=5432;Database=bankcore;Username=bankcore;Password=YOUR_LOCAL_PASSWORD'
export Jwt__SigningKey='YOUR_RANDOM_SECRET_LONGER_THAN_32_BYTES'
export ASPNETCORE_ENVIRONMENT=Development
export Database__AutoMigrate=true
dotnet run --project src/BankCore.Api --urls http://localhost:8080
```

Windows PowerShell uses `$env:ConnectionStrings__BankCore = '...'` etc. In non-development environments, migrations must be applied as an **explicit deployment step** rather than automatic startup migrations.

### Optional development demo accounts

To seed two simulated users and fund their accounts on first startup, set:

```text
Database__SeedDemoData=true
Demo__Password=your-own-demo-password-12chars-min
```

The sample users will be `demo_alice` (checking, USD 2,500) and `demo_bob` (savings, USD 1,000). **Demo seeding works only in Development**, never in real production. In Docker Compose, set `Database__SeedDemoData: "true"` under the API service and supply `BANKCORE_DEMO_PASSWORD` in `.env`. No password is embedded in source control.

## API endpoints

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/auth/register` | No | Create simulator user |
| `POST` | `/api/auth/login` | No | Get 1-hour JWT |
| `POST` | `/api/accounts` | Yes | Open checking, savings or investment account |
| `GET` | `/api/accounts` | Yes | List owned accounts |
| `GET` | `/api/accounts/{id}` | Yes | Read owned account |
| `POST` | `/api/accounts/{id}/deposits` | Yes | Simulate a deposit |
| `POST` | `/api/accounts/{id}/withdrawals` | Yes | Simulate a withdrawal |
| `POST` | `/api/transfers` | Yes | Atomic transfer, including another user's destination account |
| `GET` | `/api/accounts/{id}/ledger` | Yes | Account journal, newest first |
| `GET` | `/api/accounts/{id}/audit` | Yes | Reconcile stored balance with signed entries |
| `GET` | `/api/operations/{id}` | Yes | Read an operation initiated by you |
| `GET` | `/openapi/v1.json` | Dev | OpenAPI specification |
| `GET` | `/health` | No | Simple process liveness response (not DB readiness) |

All money-posting routes **require `Idempotency-Key: 8-128 ASCII letters/digits/-/_`** and JSON `currency: "USD"`. Users can only initiate debits/transfers from their own accounts. The simulator deposit endpoint credits owned accounts; it is **not** a real bank funding mechanism.

Example transfer:

```http
POST /api/transfers
Authorization: Bearer YOUR_ACCESS_TOKEN
Idempotency-Key: example-transfer-0001
Content-Type: application/json

{
  "fromAccountId": "SOURCE_UUID",
  "toAccountId": "DESTINATION_UUID",
  "amount": 15.25,
  "currency": "USD"
}
```

First call produces `"replayed":false`. Retrying the same key and identical payload returns the **same operation ID** and `"replayed":true`. Changing the amount or destination with that key returns `409 idempotency_conflict`.

See [`examples/BankCore.http`](examples/BankCore.http) for a complete request sequence.

## Tests

```bash
dotnet restore PADO.BankCore.sln
dotnet build PADO.BankCore.sln -c Release --no-restore
dotnet test tests/BankCore.UnitTests -c Release --no-build
```

Integration tests reset the configured database. **Never point them at important data.** Start a disposable PostgreSQL test database named `bankcore_test` (or name containing `bankcore_test`) and configure:

```bash
export BANKCORE_TEST_CONNECTION='Host=localhost;Port=5432;Database=bankcore_test;Username=bankcore;Password=YOUR_TEST_PASSWORD'
dotnet test tests/BankCore.IntegrationTests -c Release --no-build
```

These tests exercise actual row locks, rollback after SQL writes, insufficient funds, competing transfers and repeated idempotency keys. They **cannot be replaced by an EF InMemory provider** if the goal is to validate database locking.

## Scope and limitations

This simulator is intended for a C#/.NET portfolio. It has no payment rails, KYC/AML, PCI DSS assessment, regulatory certification, accounting approval workflow, distributed message queue, backup/restore protocol, automated incident response, immutable/WORM journal storage, key-rotation system, rate limiter or production infrastructure hardening. JWT shared-secret management is a development example. The balance and ledger can be changed by privileged direct SQL; the audit endpoint **detects some mismatches but does not cryptographically prevent tampering**. See [transactional design and threat model](docs/TRANSACTIONAL_DESIGN.md).

## Credits

Built from the author's original `BankSystem.zip` console programming exercise. Preserves the original account-type rule semantics, redesigned for persistence, transaction safety and integration testing.

**Author:** [PADOALEX](https://github.com/PADOALEX) · **License:** [MIT](LICENSE)
