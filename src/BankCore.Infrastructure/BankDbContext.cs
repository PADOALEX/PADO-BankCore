using BankCore.Domain;
using Microsoft.EntityFrameworkCore;

namespace BankCore.Infrastructure;

public sealed class BankDbContext(DbContextOptions<BankDbContext> options) : DbContext(options)
{
    public DbSet<BankUser> Users => Set<BankUser>();
    public DbSet<BankAccount> Accounts => Set<BankAccount>();
    public DbSet<BankOperation> Operations => Set<BankOperation>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    // Shared by the EF model snapshot: new migrations compare against the actual configured model.
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankUser>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(60).IsRequired();
            e.Property(x => x.NormalizedUsername).HasMaxLength(60).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            e.HasIndex(x => x.NormalizedUsername).IsUnique().HasDatabaseName("ux_users_normalized_username");
        });
        modelBuilder.Entity<BankAccount>(e =>
        {
            e.ToTable("accounts", b =>
            {
                b.HasCheckConstraint("ck_account_balance", "\"Balance\" >= 0 AND \"Balance\" <= 1000000000");
                b.HasCheckConstraint("ck_account_withdrawals", "\"SavingsWithdrawals\" >= 0 AND \"SavingsWithdrawals\" <= 3");
                b.HasCheckConstraint("ck_account_currency", "\"Currency\" = 'USD'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Balance).HasPrecision(19, 2);
            e.HasIndex(x => x.OwnerId);
            e.HasOne<BankUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BankOperation>(e =>
        {
            e.ToTable("operations", b =>
            {
                b.HasCheckConstraint("ck_operation_amount", "\"Amount\" > 0 AND \"Amount\" <= 1000000");
                b.HasCheckConstraint("ck_operation_currency", "\"Currency\" = 'USD'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Amount).HasPrecision(19, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.InitiatorId, x.IdempotencyKey }).IsUnique().HasDatabaseName("ux_operation_idempotency");
            e.HasOne<BankUser>().WithMany().HasForeignKey(x => x.InitiatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.SourceAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries", b => b.HasCheckConstraint("ck_ledger_nonzero", "\"SignedAmount\" <> 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.SignedAmount).HasPrecision(19, 2);
            e.Property(x => x.BalanceAfter).HasPrecision(19, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.HasIndex(x => new { x.AccountId, x.CreatedAtUtc });
            e.HasIndex(x => x.OperationId);
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BankOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
