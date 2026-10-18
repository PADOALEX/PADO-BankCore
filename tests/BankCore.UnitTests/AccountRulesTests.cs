using BankCore.Domain;
using Xunit;

namespace BankCore.UnitTests;

public sealed class AccountRulesTests
{
    [Fact]
    public void CannotSpendMoreThanBalance()
    {
        var account = BankAccount.Open(Guid.NewGuid(), AccountType.Checking);
        account.Credit(25.50m);
        var error = Assert.Throws<BankingException>(() => account.Debit(25.51m));
        Assert.Equal("insufficient_funds", error.Code);
        Assert.Equal(25.50m, account.Balance);
    }

    [Fact]
    public void SavingsAccountAllowsExactlyThreeDebits()
    {
        var account = BankAccount.Open(Guid.NewGuid(), AccountType.Savings);
        account.Credit(100m);
        for (var i = 0; i < 3; i++) account.Debit(10m);
        Assert.Equal("withdrawal_limit", Assert.Throws<BankingException>(() => account.Debit(1m)).Code);
        Assert.Equal(70m, account.Balance);
        Assert.Equal(3, account.SavingsWithdrawals);
    }

    [Fact]
    public void FailedSavingsDebitDoesNotConsumeWithdrawal()
    {
        var account = BankAccount.Open(Guid.NewGuid(), AccountType.Savings);
        Assert.Throws<BankingException>(() => account.Debit(1m));
        Assert.Equal(0, account.SavingsWithdrawals);
    }

    [Fact]
    public void InvestmentAccountRetainsMinimumBalance()
    {
        var account = BankAccount.Open(Guid.NewGuid(), AccountType.Investment);
        account.Credit(1500m);
        account.Debit(500m);
        Assert.Equal("minimum_balance", Assert.Throws<BankingException>(() => account.Debit(.01m)).Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("1.001")]
    [InlineData("1000000.01")]
    public void RejectsInvalidAmount(string raw)
    {
        var amount = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("invalid_amount", Assert.Throws<BankingException>(() => MoneyPolicy.Validate(amount, "USD")).Code);
    }

    [Fact]
    public void IdempotencyHashUsesCanonicalMonetaryScale()
    {
        var id = Guid.NewGuid();
        var a = MoneyPolicy.Fingerprint(OperationType.Deposit, id, null, 10m, "USD");
        var b = MoneyPolicy.Fingerprint(OperationType.Deposit, id, null, 10.00m, "USD");
        Assert.Equal(a, b);
    }
}
