using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BankCore.Infrastructure.Migrations;

[DbContext(typeof(BankDbContext))]
[Migration("202610180001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE users (
              "Id" uuid NOT NULL PRIMARY KEY,
              "Username" varchar(60) NOT NULL,
              "NormalizedUsername" varchar(60) NOT NULL,
              "PasswordHash" varchar(500) NOT NULL,
              "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX ux_users_normalized_username ON users ("NormalizedUsername");

            CREATE TABLE accounts (
              "Id" uuid NOT NULL PRIMARY KEY,
              "OwnerId" uuid NOT NULL REFERENCES users("Id") ON DELETE RESTRICT,
              "Type" varchar(24) NOT NULL,
              "Currency" varchar(3) NOT NULL,
              "Balance" numeric(19,2) NOT NULL,
              "SavingsWithdrawals" integer NOT NULL,
              "CreatedAtUtc" timestamp with time zone NOT NULL,
              CONSTRAINT ck_account_balance CHECK ("Balance" >= 0 AND "Balance" <= 1000000000),
              CONSTRAINT ck_account_withdrawals CHECK ("SavingsWithdrawals" >= 0 AND "SavingsWithdrawals" <= 3),
              CONSTRAINT ck_account_currency CHECK ("Currency" = 'USD')
            );
            CREATE INDEX ix_accounts_owner ON accounts ("OwnerId");

            CREATE TABLE operations (
              "Id" uuid NOT NULL PRIMARY KEY,
              "InitiatorId" uuid NOT NULL REFERENCES users("Id") ON DELETE RESTRICT,
              "IdempotencyKey" varchar(128) NOT NULL,
              "RequestHash" varchar(64) NOT NULL,
              "Type" varchar(24) NOT NULL,
              "SourceAccountId" uuid NOT NULL REFERENCES accounts("Id") ON DELETE RESTRICT,
              "DestinationAccountId" uuid NULL REFERENCES accounts("Id") ON DELETE RESTRICT,
              "Amount" numeric(19,2) NOT NULL,
              "Currency" varchar(3) NOT NULL,
              "CreatedAtUtc" timestamp with time zone NOT NULL,
              CONSTRAINT ck_operation_amount CHECK ("Amount" > 0 AND "Amount" <= 1000000),
              CONSTRAINT ck_operation_currency CHECK ("Currency" = 'USD')
            );
            CREATE UNIQUE INDEX ux_operation_idempotency ON operations ("InitiatorId", "IdempotencyKey");
            CREATE INDEX ix_operations_source ON operations ("SourceAccountId");
            CREATE INDEX ix_operations_destination ON operations ("DestinationAccountId");

            CREATE TABLE ledger_entries (
              "Id" uuid NOT NULL PRIMARY KEY,
              "OperationId" uuid NOT NULL REFERENCES operations("Id") ON DELETE RESTRICT,
              "AccountId" uuid NOT NULL REFERENCES accounts("Id") ON DELETE RESTRICT,
              "Type" varchar(24) NOT NULL,
              "SignedAmount" numeric(19,2) NOT NULL,
              "BalanceAfter" numeric(19,2) NOT NULL,
              "Currency" varchar(3) NOT NULL,
              "CreatedAtUtc" timestamp with time zone NOT NULL,
              CONSTRAINT ck_ledger_nonzero CHECK ("SignedAmount" <> 0)
            );
            CREATE INDEX ix_ledger_entries_account_date ON ledger_entries ("AccountId", "CreatedAtUtc");
            CREATE INDEX ix_ledger_entries_operation ON ledger_entries ("OperationId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE ledger_entries;
            DROP TABLE operations;
            DROP TABLE accounts;
            DROP TABLE users;
            """);
    }
}
