using System.Data;
using BankCore.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BankCore.Infrastructure;

public sealed record PostingRequest(Guid UserId, OperationType Type, Guid SourceId, Guid? DestinationId, decimal Amount, string Currency, string IdempotencyKey);
public sealed record PostingReceipt(Guid OperationId, OperationType Type, Guid SourceId, Guid? DestinationId, decimal Amount, string Currency, DateTimeOffset CreatedAtUtc, bool Replayed);

/// <summary>
/// Every monetary mutation happens under PostgreSQL row locks and one DB transaction.
/// No process-local locks are used. The idempotency constraint is a second line of defense.
/// </summary>
public interface IPostingFaultInjector
{
    Task BeforeCommitAsync(CancellationToken ct);
}

public sealed class PostingService(BankDbContext db, IPostingFaultInjector? faultInjector = null)
{
    public async Task<PostingReceipt> PostAsync(PostingRequest request, CancellationToken ct = default)
    {
        MoneyPolicy.Validate(request.Amount, request.Currency);
        MoneyPolicy.ValidateKey(request.IdempotencyKey);
        if (request.Type == OperationType.Transfer && (request.DestinationId is null || request.SourceId == request.DestinationId))
            throw new BankingException("invalid_transfer", "Transfer requires two different accounts.");
        if (request.Type != OperationType.Transfer && request.DestinationId is not null)
            throw new BankingException("invalid_operation", "Only transfers have a destination account.");

        var hash = MoneyPolicy.Fingerprint(request.Type, request.SourceId, request.DestinationId, request.Amount, request.Currency);
        try
        {
            return await ExecuteTransactionAsync(request, hash, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_operation_idempotency" })
        {
            // Competing identical requests can lock unrelated accounts and race on the unique key.
            // The loser rolls back before checking the winner's committed result.
            db.ChangeTracker.Clear();
            var committed = await db.Operations.AsNoTracking().SingleOrDefaultAsync(
                x => x.InitiatorId == request.UserId && x.IdempotencyKey == request.IdempotencyKey, ct);
            if (committed is null)
                throw new BankingException("idempotency_race", "Retry this request using the same idempotency key.");
            return ToReceipt(VerifyMatchingRequest(committed, hash), replayed: true);
        }
    }

    private async Task<PostingReceipt> ExecuteTransactionAsync(PostingRequest request, string hash, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var previous = await db.Operations.AsNoTracking().SingleOrDefaultAsync(
            x => x.InitiatorId == request.UserId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (previous is not null)
            return ToReceipt(VerifyMatchingRequest(previous, hash), replayed: true);

        var accountIds = new[] { request.SourceId, request.DestinationId ?? request.SourceId }
            .Distinct().OrderBy(x => x).ToArray();
        var locked = new Dictionary<Guid, BankAccount>();
        foreach (var id in accountIds)
        {
            // Sorting the IDs gives all API instances the same lock acquisition order.
            var account = await db.Accounts
                .FromSqlInterpolated($"SELECT * FROM accounts WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (account is null)
                throw new BankingException("account_not_found", "Account not found.");
            locked.Add(id, account);
        }

        // A competing request using the same source/key may have committed while we waited.
        previous = await db.Operations.AsNoTracking().SingleOrDefaultAsync(
            x => x.InitiatorId == request.UserId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (previous is not null)
            return ToReceipt(VerifyMatchingRequest(previous, hash), replayed: true);

        var source = locked[request.SourceId];
        if (source.OwnerId != request.UserId)
            throw new BankingException("forbidden_account", "Only the account owner may initiate operations.");
        if (source.Currency != request.Currency)
            throw new BankingException("currency_mismatch", "Account currency differs from request currency.");
        BankAccount? destination = null;
        if (request.DestinationId is Guid targetId)
        {
            destination = locked[targetId];
            if (destination.Currency != request.Currency)
                throw new BankingException("currency_mismatch", "Cross-currency transfers are not supported.");
        }

        var operation = BankOperation.Create(request.UserId, request.IdempotencyKey, hash,
            request.Type, source.Id, destination?.Id, request.Amount);
        db.Operations.Add(operation);
        switch (request.Type)
        {
            case OperationType.Deposit:
                source.Credit(request.Amount);
                db.LedgerEntries.Add(LedgerEntry.Record(operation, source, EntryType.Deposit, request.Amount));
                break;
            case OperationType.Withdrawal:
                source.Debit(request.Amount);
                db.LedgerEntries.Add(LedgerEntry.Record(operation, source, EntryType.Withdrawal, -request.Amount));
                break;
            case OperationType.Transfer:
                source.Debit(request.Amount);
                destination!.Credit(request.Amount);
                db.LedgerEntries.Add(LedgerEntry.Record(operation, source, EntryType.TransferDebit, -request.Amount));
                db.LedgerEntries.Add(LedgerEntry.Record(operation, destination, EntryType.TransferCredit, request.Amount));
                break;
            default:
                throw new BankingException("invalid_operation", "Unsupported operation type.");
        }

        await db.SaveChangesAsync(ct);
        if (faultInjector is not null) await faultInjector.BeforeCommitAsync(ct);
        await tx.CommitAsync(ct);
        return ToReceipt(operation, replayed: false);
    }

    private static BankOperation VerifyMatchingRequest(BankOperation operation, string hash)
    {
        if (!string.Equals(operation.RequestHash, hash, StringComparison.Ordinal))
            throw new BankingException("idempotency_conflict", "Idempotency key already used with a different request.");
        return operation;
    }

    private static PostingReceipt ToReceipt(BankOperation operation, bool replayed) => new(
        operation.Id, operation.Type, operation.SourceAccountId, operation.DestinationAccountId,
        operation.Amount, operation.Currency, operation.CreatedAtUtc, replayed);
}
