using System.Data.Common;
using BankCore.Domain;
using BankCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace BankCore.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class LedgerAuditIntegrationTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task OppositeTransfersPreserveBalancesAndCompleteWithoutDeadlock()
    {
        var accounts = new List<(Guid Owner, Guid Account)>();
        await using (var setup = fixture.NewContext())
        {
            for (var index = 0; index < 2; index++)
            {
                var owner = BankUser.Create("opposite" + Guid.NewGuid().ToString("N")[..12]);
                var account = BankAccount.Open(owner.Id, AccountType.Checking);
                setup.Users.Add(owner);
                setup.Accounts.Add(account);
                await setup.SaveChangesAsync();
                await new PostingService(setup).PostAsync(new(owner.Id, OperationType.Deposit, account.Id, null, 100m, "USD", Guid.NewGuid().ToString("N")));
                accounts.Add((owner.Id, account.Id));
            }
        }
        async Task Transfer(int source, int number)
        {
            await using var db = fixture.NewContext();
            await new PostingService(db).PostAsync(new(accounts[source].Owner, OperationType.Transfer,
                accounts[source].Account, accounts[1 - source].Account, 1m, "USD", $"opposite-{Guid.NewGuid():N}-{number}"));
        }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(number => Transfer(number % 2, number)))
            .WaitAsync(TimeSpan.FromSeconds(20));
        foreach (var account in accounts)
        {
            await using var db = fixture.NewContext();
            var report = await new LedgerAuditService(db).VerifyAccountAsync(account.Owner, account.Account);
            Assert.Equal(100m, report.StoredBalance);
            Assert.True(report.BalanceMatchesLedger);
            Assert.Equal(21, report.EntryCount);
            Assert.Equal(0, report.InvalidTransferCount);
        }
    }

    [Fact]
    public async Task AuditUsesOneSnapshotWhileAnotherConnectionPosts()
    {
        await using var setup = fixture.NewContext();
        var owner = BankUser.Create("audit" + Guid.NewGuid().ToString("N")[..15]);
        var account = BankAccount.Open(owner.Id, AccountType.Checking);
        setup.Users.Add(owner);
        setup.Accounts.Add(account);
        await setup.SaveChangesAsync();
        await new PostingService(setup).PostAsync(new(owner.Id, OperationType.Deposit, account.Id, null, 100m, "USD", Guid.NewGuid().ToString("N")));

        var checkpoint = new BeforeLedgerRead();
        var options = new DbContextOptionsBuilder<BankDbContext>()
            .UseNpgsql(setup.Database.GetConnectionString()).AddInterceptors(checkpoint).Options;
        await using var auditDb = new BankDbContext(options);
        var auditTask = new LedgerAuditService(auditDb).VerifyAccountAsync(owner.Id, account.Id);
        await checkpoint.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var writer = fixture.NewContext();
            await new PostingService(writer).PostAsync(new(owner.Id, OperationType.Deposit, account.Id, null, 25m, "USD", Guid.NewGuid().ToString("N")));
        }
        finally
        {
            checkpoint.Resume.TrySetResult();
        }
        var snapshot = await auditTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(snapshot.BalanceMatchesLedger);
        Assert.Equal(100m, snapshot.StoredBalance);
        Assert.Equal(100m, snapshot.SignedLedgerTotal);
        Assert.Equal(1, snapshot.EntryCount);
        await using var latestDb = fixture.NewContext();
        var latest = await new LedgerAuditService(latestDb).VerifyAccountAsync(owner.Id, account.Id);
        Assert.True(latest.BalanceMatchesLedger);
        Assert.Equal(125m, latest.StoredBalance);
        Assert.Equal(2, latest.EntryCount);
    }

    private sealed class BeforeLedgerRead : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sum(", StringComparison.OrdinalIgnoreCase))
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
