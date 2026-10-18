using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BankCore.Domain;

public enum AccountType { Checking, Savings, Investment }
public enum OperationType { Deposit, Withdrawal, Transfer }
public enum EntryType { Deposit, Withdrawal, TransferDebit, TransferCredit }

public sealed class BankingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static partial class MoneyPolicy
{
    public const string Currency = "USD";
    public const decimal MaximumAmount = 1_000_000m;
    public const decimal MaximumBalance = 1_000_000_000m;
    public const decimal InvestmentMinimumBalance = 1_000m;

    public static void Validate(decimal amount, string currency)
    {
        if (currency != Currency)
            throw new BankingException("unsupported_currency", "Only USD accounts and transactions are supported.");
        if (amount <= 0 || amount > MaximumAmount || decimal.Round(amount, 2) != amount)
            throw new BankingException("invalid_amount", "Amount must be greater than zero, at most 1,000,000, and have at most two decimal places.");
    }

    public static void ValidateKey(string key)
    {
        if (key is null || !IdempotencyKeyPattern().IsMatch(key))
            throw new BankingException("invalid_idempotency_key", "Idempotency-Key must contain 8-128 letters, digits, '-' or '_'.");
    }

    public static string Fingerprint(OperationType type, Guid source, Guid? destination, decimal amount, string currency)
    {
        var canonical = string.Join("|", type, source.ToString("N"), destination?.ToString("N") ?? "", amount.ToString("F2", CultureInfo.InvariantCulture), currency);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyKeyPattern();
}

public sealed class BankUser
{
    private BankUser() { }
    public Guid Id { get; private set; }
    public string Username { get; private set; } = "";
    public string NormalizedUsername { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static BankUser Create(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length is < 3 or > 60 ||
            !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new BankingException("invalid_username", "Username must have 3-60 ASCII letters, digits, '-' or '_'.");
        return new BankUser
        {
            Id = Guid.NewGuid(), Username = username,
            NormalizedUsername = username.ToUpperInvariant(), CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public void SetPasswordHash(string hash) => PasswordHash = hash;
}

public sealed class BankAccount
{
    private BankAccount() { }
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public AccountType Type { get; private set; }
    public string Currency { get; private set; } = MoneyPolicy.Currency;
    public decimal Balance { get; private set; }
    public int SavingsWithdrawals { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static BankAccount Open(Guid ownerId, AccountType type) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, Type = type, Balance = 0m,
        Currency = MoneyPolicy.Currency, CreatedAtUtc = DateTimeOffset.UtcNow
    };

    public void Credit(decimal amount)
    {
        MoneyPolicy.Validate(amount, Currency);
        if (Balance + amount > MoneyPolicy.MaximumBalance)
            throw new BankingException("balance_limit", "Maximum allowed account balance exceeded.");
        Balance += amount;
    }

    public void Debit(decimal amount)
    {
        MoneyPolicy.Validate(amount, Currency);
        if (Balance < amount)
            throw new BankingException("insufficient_funds", "Insufficient funds.");
        if (Type == AccountType.Savings && SavingsWithdrawals >= 3)
            throw new BankingException("withdrawal_limit", "Savings account permits three lifetime debits.");
        if (Type == AccountType.Investment && Balance - amount < MoneyPolicy.InvestmentMinimumBalance)
            throw new BankingException("minimum_balance", "Investment account must retain at least USD 1,000.");
        Balance -= amount;
        if (Type == AccountType.Savings) SavingsWithdrawals++;
    }
}

public sealed class BankOperation
{
    private BankOperation() { }
    public Guid Id { get; private set; }
    public Guid InitiatorId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string RequestHash { get; private set; } = "";
    public OperationType Type { get; private set; }
    public Guid SourceAccountId { get; private set; }
    public Guid? DestinationAccountId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = MoneyPolicy.Currency;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static BankOperation Create(Guid userId, string key, string hash, OperationType type, Guid sourceId, Guid? targetId, decimal amount) => new()
    {
        Id = Guid.NewGuid(), InitiatorId = userId, IdempotencyKey = key,
        RequestHash = hash, Type = type, SourceAccountId = sourceId,
        DestinationAccountId = targetId, Amount = amount,
        Currency = MoneyPolicy.Currency, CreatedAtUtc = DateTimeOffset.UtcNow
    };
}

public sealed class LedgerEntry
{
    private LedgerEntry() { }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid AccountId { get; private set; }
    public EntryType Type { get; private set; }
    public decimal SignedAmount { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public string Currency { get; private set; } = MoneyPolicy.Currency;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static LedgerEntry Record(BankOperation operation, BankAccount account, EntryType type, decimal signedAmount) => new()
    {
        Id = Guid.NewGuid(), OperationId = operation.Id, AccountId = account.Id,
        Type = type, SignedAmount = signedAmount, BalanceAfter = account.Balance,
        Currency = MoneyPolicy.Currency, CreatedAtUtc = operation.CreatedAtUtc
    };
}
