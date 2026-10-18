using BankCore.Domain;
using BankCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BankCore.IntegrationTests;

// Requires a REAL PostgreSQL database. No mocks / in-memory EF provider in correctness tests.
[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<DatabaseFixture> { }

public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly string _connectionString = Environment.GetEnvironmentVariable("BANKCORE_TEST_CONNECTION")
        ?? throw new InvalidOperationException("Set BANKCORE_TEST_CONNECTION to a disposable PostgreSQL test database.");

    public BankDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<BankDbContext>().UseNpgsql(_connectionString).Options;
        return new BankDbContext(options);
    }

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database) || !builder.Database.Contains("bankcore_test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to reset a database that is not named '*bankcore_test*'.");
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[Collection("PostgreSQL")]
public sealed class PostingIntegrationTests(DatabaseFixture fixture)
{
    private async Task<(Guid user, Guid from, Guid to)> SetupAsync(decimal initialBalance = 100m)
    {
        await using var db = fixture.NewContext();
        var user = BankUser.Create("u" + Guid.NewGuid().ToString("N")[..16]);
        var recipient = BankUser.Create("r" + Guid.NewGuid().ToString("N")[..16]);
        var from = BankAccount.Open(user.Id, AccountType.Checking);
        var to = BankAccount.Open(recipient.Id, AccountType.Checking);
        db.Users.AddRange(user, recipient);
        db.Accounts.AddRange(from, to);
        await db.SaveChangesAsync();
        var service = new PostingService(db);
        await service.PostAsync(new PostingRequest(user.Id, OperationType.Deposit, from.Id, null, initialBalance, "USD", Guid.NewGuid().ToString("N")));
        return (user.Id, from.Id, to.Id);
    }

    [Fact]
    public async Task TransferPostsTwoBalancedEntriesAndPreservesTotal()
    {
        var (user, from, to) = await SetupAsync();
        await using var db = fixture.NewContext();
        var receipt = await new PostingService(db).PostAsync(
            new(user, OperationType.Transfer, from, to, 40m, "USD", "transfer-case-0001"));
        var entries = await db.LedgerEntries.AsNoTracking().Where(x => x.OperationId == receipt.OperationId).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0m, entries.Sum(x => x.SignedAmount));
        Assert.Equal(60m, (await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == from)).Balance);
        Assert.Equal(40m, (await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == to)).Balance);
    }

    [Fact]
    public async Task RepeatedKeyDoesNotDebitTwiceAndRejectsChangedPayload()
    {
        var (user, from, to) = await SetupAsync();
        const string key = "repeat-key-case-001";
        var req = new PostingRequest(user, OperationType.Transfer, from, to, 30m, "USD", key);
        await using var one = fixture.NewContext();
        var first = await new PostingService(one).PostAsync(req);
        await using var two = fixture.NewContext();
        var second = await new PostingService(two).PostAsync(req);
        Assert.True(second.Replayed);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal("idempotency_conflict", (await Assert.ThrowsAsync<BankingException>(
            () => new PostingService(two).PostAsync(req with { Amount = 31m }))).Code);
        Assert.Equal(1, await two.Operations.CountAsync(x => x.InitiatorId == user && x.IdempotencyKey == key));
    }

    [Fact]
    public async Task ParallelDifferentKeysCannotOverdraw()
    {
        var (user, from, to) = await SetupAsync();
        async Task<bool> Attempt(string key)
        {
            await using var db = fixture.NewContext();
            try
            {
                await new PostingService(db).PostAsync(new(user, OperationType.Transfer, from, to, 80m, "USD", key));
                return true;
            }
            catch (BankingException ex) when (ex.Code == "insufficient_funds") { return false; }
        }
        var results = await Task.WhenAll(Attempt("concurrent-a-001"), Attempt("concurrent-b-001"));
        Assert.Single(results, x => x);
        await using var inspect = fixture.NewContext();
        Assert.Equal(20m, (await inspect.Accounts.AsNoTracking().SingleAsync(x => x.Id == from)).Balance);
        Assert.Equal(80m, (await inspect.Accounts.AsNoTracking().SingleAsync(x => x.Id == to)).Balance);
    }

    [Fact]
    public async Task ParallelSameKeyProducesSingleCommittedOperation()
    {
        var (user, from, to) = await SetupAsync();
        var request = new PostingRequest(user, OperationType.Transfer, from, to, 25m, "USD", "same-key-parallel-001");
        async Task<PostingReceipt> Attempt()
        {
            await using var db = fixture.NewContext();
            return await new PostingService(db).PostAsync(request);
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Equal(results[0].OperationId, results[1].OperationId);
        Assert.Single(results, x => !x.Replayed);
        await using var inspect = fixture.NewContext();
        Assert.Equal(75m, (await inspect.Accounts.AsNoTracking().SingleAsync(x => x.Id == from)).Balance);
        Assert.Equal(1, await inspect.Operations.CountAsync(x => x.IdempotencyKey == request.IdempotencyKey && x.InitiatorId == user));
    }

    [Fact]
    public async Task FailureAfterSqlWritesRollsBackBalancesOperationAndJournal()
    {
        var (user, from, to) = await SetupAsync();
        await using (var db = fixture.NewContext())
        {
            var service = new PostingService(db, new SimulatedFailure());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostAsync(
                new(user, OperationType.Transfer, from, to, 60m, "USD", "rollback-test-0001")));
        }
        await using var inspect = fixture.NewContext();
        Assert.Equal(100m, (await inspect.Accounts.AsNoTracking().SingleAsync(x => x.Id == from)).Balance);
        Assert.Equal(0m, (await inspect.Accounts.AsNoTracking().SingleAsync(x => x.Id == to)).Balance);
        Assert.False(await inspect.Operations.AnyAsync(x => x.IdempotencyKey == "rollback-test-0001"));
        Assert.False(await inspect.LedgerEntries.AnyAsync(x => x.SignedAmount == -60m));
    }

    [Fact]
    public async Task OtherUserCannotOperateOnAnAccount()
    {
        var (user, from, _) = await SetupAsync();
        var other = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var ex = await Assert.ThrowsAsync<BankingException>(() => new PostingService(db).PostAsync(
            new(other, OperationType.Withdrawal, from, null, 1m, "USD", "not-owner-0001")));
        Assert.Equal("forbidden_account", ex.Code);
        _ = user;
    }

    private sealed class SimulatedFailure : IPostingFaultInjector
    {
        public Task BeforeCommitAsync(CancellationToken ct) => throw new InvalidOperationException("Injected failure before COMMIT");
    }
}
