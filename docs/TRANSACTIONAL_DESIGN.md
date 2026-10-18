# Transactional design and correctness boundaries

## Explicit money policy

- All amounts are `decimal`, persisted as PostgreSQL `numeric(19,2)`.
- Only `USD`. Input with more than two fractional digits is **rejected**, never silently rounded.
- Transactions require amounts in `(0, 1_000_000]`; balances must remain in `[0, 1_000_000_000]`.
- No exchange rates, interest accrual, overdrafts, fees, refunds, chargebacks or real-money settlement.
- Savings permits **three successful lifetime debits**, including outbound transfers. Investment debits must leave at least USD 1,000.
- Account creation starts at zero. Simulator deposits create corresponding positive journal entries; no balance is silently minted on account creation.

## Commit boundary

For each posting:

1. Validate currency, amount and idempotency-key format.
2. Begin PostgreSQL `READ COMMITTED` transaction.
3. Check if `(initiator, key)` is already committed; return the stored result or reject conflicting payload.
4. Acquire `SELECT * FROM accounts WHERE "Id" = @id FOR UPDATE` locks **in sorted GUID order** on source/destination (one lock for same-account operations).
5. Check the idempotency key again after waiting, so an in-flight identical request cannot double-post.
6. Validate account existence, ownership of source, currency, funds, account-specific limits and recipient capacity.
7. Mutate tracked account balances and counters. Insert an operation row and ledger entries.
8. `SaveChangesAsync`, then `CommitAsync`. If either fails, transaction disposal rolls back all of the writes.

The DB unique index on `(InitiatorId, IdempotencyKey)` catches same-key attempts involving unrelated accounts that might not compete on row locks. On its violation, the rolled-back loser reads the committed operation and compares canonical request hash. Any other database failure is **not** treated as success.

### Why `lock` was removed

C# `lock` only synchronizes threads that share one in-process object. Separate API hosts do not share that object. PostgreSQL row locks are coordinated by the database even across independent server processes. The integration tests use independent EF contexts and separate connections and check that only one simultaneous overspending transfer can succeed.

### Journal invariants

- Each successful transfer writes precisely two legs in the **same** commit: negative source amount and positive destination amount. The sum of signed legs equals zero.
- Deposits write one positive entry; withdrawals one negative entry. These are explicitly simulated inflows/outflows, not conserved system funds.
- No failed operation writes an operation receipt or journal entry.
- Replays produce no additional writes.
- For accounts opened at zero with all changes made through `PostingService`, `Balance == SUM(SignedAmount)`.
- `GET /api/accounts/{id}/audit` reports reconciliation and checks that each transfer touching the account has two opposite-signed entries. This is a diagnostic check, **not** a cryptographically tamper-proof ledger.

### Error semantics and ambiguous failures

- A timeout or connection loss after COMMIT may make the client uncertain whether the transaction committed. The client **must retry with the same idempotency key**; the server will return the existing operation if committed.
- If the database aborts the transaction, all SQL writes roll back. The client may retry the **same** key when the error is transient.
- A request with the same key but different content is rejected (`409`). The fingerprint includes operation type, source, destination, canonical monetary amount and currency.
- The API never returns a successful receipt for a failed SQL transaction.

### Deliberately excluded from this portfolio MVP

- Actual deposit funding authorization, banking integrations, KYC/AML or legal compliance.
- Cross-currency transfers, time-limited savings withdrawal windows or investment interest calculation.
- Real banking durability/DR certification, multi-region consistency, robust observability and alerting.
- Tamper-proof journal: privileged DB access can rewrite history. A production system would additionally require append-only enforcement, privileged separation, hash chaining/anchoring, reconciliation controls and independent backups.
- Fine-grained operational IAM, revocation/refresh tokens, login throttling and a proper secrets manager.

## Migration safety

`Database:AutoMigrate` is only respected in the `Development` environment. CI runs against its own disposable PostgreSQL test DB and explicitly applies EF migrations. Schema and test behavior must be verified via `dotnet test` with PostgreSQL before calling the implementation validated.
