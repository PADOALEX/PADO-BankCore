using BankCore.Domain;
using Microsoft.EntityFrameworkCore;

namespace BankCore.Infrastructure;

public sealed record LedgerAuditReport(Guid AccountId, decimal StoredBalance, decimal SignedLedgerTotal,
    bool BalanceMatchesLedger, int EntryCount, int InvalidTransferCount);

public sealed class LedgerAuditService(BankDbContext db)
{
    public async Task<LedgerAuditReport> VerifyAccountAsync(Guid ownerId, Guid accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId && x.OwnerId == ownerId, ct)
            ?? throw new BankingException("account_not_found", "Account not found.");
        var ledger = db.LedgerEntries.AsNoTracking().Where(x => x.AccountId == accountId);
        var total = await ledger.SumAsync(x => (decimal?)x.SignedAmount, ct) ?? 0m;
        var count = await ledger.CountAsync(ct);
        var ids = await db.Operations.AsNoTracking()
            .Where(x => x.Type == OperationType.Transfer && (x.SourceAccountId == accountId || x.DestinationAccountId == accountId))
            .Select(x => x.Id).ToListAsync(ct);
        var transferLegs = await db.LedgerEntries.AsNoTracking()
            .Where(x => ids.Contains(x.OperationId))
            .GroupBy(x => x.OperationId)
            .Select(g => new { Id = g.Key, Count = g.Count(), SignedTotal = g.Sum(x => x.SignedAmount) })
            .ToListAsync(ct);
        var broken = ids.Count - transferLegs.Count + transferLegs.Count(x => x.Count != 2 || x.SignedTotal != 0m);
        return new LedgerAuditReport(accountId, account.Balance, total, account.Balance == total, count, broken);
    }
}
