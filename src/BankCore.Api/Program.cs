using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BankCore.Domain;
using BankCore.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connection = builder.Configuration.GetConnectionString("BankCore");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("ConnectionStrings:BankCore must be configured via environment variables or secrets.");
var jwtKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be supplied and contain at least 32 UTF-8 bytes.");
const string issuer = "PADO.BankCore";
const string audience = "PADO.BankCore.Api";

builder.Services.AddDbContext<BankDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddScoped<PostingService>();
builder.Services.AddScoped<LedgerAuditService>();
builder.Services.AddScoped<IPasswordHasher<BankUser>, PasswordHasher<BankUser>>();
builder.Services.AddOpenApi();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = issuer,
            ValidateAudience = true, ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (BankingException ex)
    {
        var status = ex.Code switch
        {
            "forbidden_account" => StatusCodes.Status403Forbidden,
            "account_not_found" => StatusCodes.Status404NotFound,
            "insufficient_funds" or "balance_limit" or "minimum_balance" or "withdrawal_limit" or "idempotency_conflict" or "idempotency_race" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
    }
});
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BankDbContext>().Database.MigrateAsync();
    }
    if (app.Configuration.GetValue<bool>("Database:SeedDemoData"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var demoPassword = app.Configuration["Demo:Password"];
        if (string.IsNullOrWhiteSpace(demoPassword) || demoPassword.Length < 12)
            throw new InvalidOperationException("Demo:Password must be set to at least 12 characters when demo seeding is enabled.");
        await SeedDemoAsync(scope.ServiceProvider.GetRequiredService<BankDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<BankUser>>(), demoPassword);
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", simulation = true }))
    .AllowAnonymous();

var auth = app.MapGroup("/api/auth");
auth.MapPost("/register", async (RegisterRequest request, BankDbContext db, IPasswordHasher<BankUser> hasher, CancellationToken ct) =>
{
    if (request.Password is null || request.Password.Length < 12 || request.Password.Length > 128)
        return Results.BadRequest(new { code = "invalid_password", message = "Password must contain between 12 and 128 characters." });
    var user = BankUser.Create(request.Username);
    user.SetPasswordHash(hasher.HashPassword(user, request.Password));
    db.Users.Add(user);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    { return Results.Conflict(new { code = "username_taken" }); }
    return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username });
}).AllowAnonymous();
auth.MapPost("/login", async (LoginRequest request, BankDbContext db, IPasswordHasher<BankUser> hasher, CancellationToken ct) =>
{
    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedUsername == request.Username.ToUpper(), ct);
    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();
    var expires = DateTime.UtcNow.AddHours(1);
    var token = new JwtSecurityToken(issuer, audience,
        [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(JwtRegisteredClaimNames.UniqueName, user.Username)],
        expires: expires, signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)), SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), tokenType = "Bearer", expiresAtUtc = expires });
}).AllowAnonymous();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapPost("/accounts", async (CreateAccountRequest request, ClaimsPrincipal user, BankDbContext db, CancellationToken ct) =>
{
    if (!Enum.TryParse<AccountType>(request.Type, true, out var type) || !Enum.IsDefined(type))
        return Results.BadRequest(new { code = "invalid_account_type" });
    var account = BankAccount.Open(UserId(user), type);
    db.Accounts.Add(account);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/accounts/{account.Id}", ToAccount(account));
});
api.MapGet("/accounts", async (ClaimsPrincipal user, BankDbContext db, CancellationToken ct) =>
    Results.Ok((await db.Accounts.AsNoTracking().Where(x => x.OwnerId == UserId(user)).OrderBy(x => x.CreatedAtUtc)
        .ToListAsync(ct)).Select(ToAccount)));
api.MapGet("/accounts/{id:guid}", async (Guid id, ClaimsPrincipal user, BankDbContext db, CancellationToken ct) =>
{
    var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == UserId(user), ct);
    return account is null ? Results.NotFound() : Results.Ok(ToAccount(account));
});
api.MapPost("/accounts/{id:guid}/deposits", async (Guid id, AmountRequest req, HttpContext http, ClaimsPrincipal user, PostingService service, CancellationToken ct) =>
    Results.Ok(await service.PostAsync(new PostingRequest(UserId(user), OperationType.Deposit, id, null, req.Amount, req.Currency, Key(http)), ct)));
api.MapPost("/accounts/{id:guid}/withdrawals", async (Guid id, AmountRequest req, HttpContext http, ClaimsPrincipal user, PostingService service, CancellationToken ct) =>
    Results.Ok(await service.PostAsync(new PostingRequest(UserId(user), OperationType.Withdrawal, id, null, req.Amount, req.Currency, Key(http)), ct)));
api.MapPost("/transfers", async (TransferRequest req, HttpContext http, ClaimsPrincipal user, PostingService service, CancellationToken ct) =>
    Results.Ok(await service.PostAsync(new PostingRequest(UserId(user), OperationType.Transfer, req.FromAccountId, req.ToAccountId, req.Amount, req.Currency, Key(http)), ct)));
api.MapGet("/accounts/{id:guid}/audit", async (Guid id, ClaimsPrincipal user, LedgerAuditService audit, CancellationToken ct) =>
    Results.Ok(await audit.VerifyAccountAsync(UserId(user), id, ct)));
api.MapGet("/accounts/{id:guid}/ledger", async (Guid id, int? limit, ClaimsPrincipal user, BankDbContext db, CancellationToken ct) =>
{
    if (!await db.Accounts.AnyAsync(x => x.Id == id && x.OwnerId == UserId(user), ct))
        return Results.NotFound();
    var pageSize = Math.Clamp(limit ?? 50, 1, 100);
    var entries = await db.LedgerEntries.AsNoTracking().Where(x => x.AccountId == id)
        .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
        .Take(pageSize).Select(x => new { x.Id, x.OperationId, x.Type, x.SignedAmount, x.BalanceAfter, x.Currency, x.CreatedAtUtc })
        .ToListAsync(ct);
    return Results.Ok(entries);
});
api.MapGet("/operations/{id:guid}", async (Guid id, ClaimsPrincipal user, BankDbContext db, CancellationToken ct) =>
{
    var op = await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.InitiatorId == UserId(user), ct);
    if (op is null) return Results.NotFound();
    var entryCount = await db.LedgerEntries.CountAsync(x => x.OperationId == id, ct);
    return Results.Ok(new
    {
        op.Id, op.Type, op.SourceAccountId, op.DestinationAccountId, op.Amount, op.Currency, op.CreatedAtUtc,
        entryCount
    });
});

await app.RunAsync();

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
static string Key(HttpContext ctx) => ctx.Request.Headers["Idempotency-Key"].ToString();
static object ToAccount(BankAccount a) => new { a.Id, a.OwnerId, a.Type, a.Currency, a.Balance, a.SavingsWithdrawals, a.CreatedAtUtc };

public sealed record RegisterRequest(string Username, string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record CreateAccountRequest(string Type);
public sealed record AmountRequest(decimal Amount, string Currency);
public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, decimal Amount, string Currency);

static async Task SeedDemoAsync(BankDbContext db, IPasswordHasher<BankUser> hasher, string password)
{
    foreach (var (name, type, opening) in new[]
    {
        ("demo_alice", AccountType.Checking, 2500m),
        ("demo_bob", AccountType.Savings, 1000m)
    })
    {
        if (await db.Users.AnyAsync(x => x.NormalizedUsername == name.ToUpperInvariant())) continue;
        var user = BankUser.Create(name);
        user.SetPasswordHash(hasher.HashPassword(user, password));
        var account = BankAccount.Open(user.Id, type);
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        await new PostingService(db).PostAsync(new PostingRequest(user.Id, OperationType.Deposit,
            account.Id, null, opening, "USD", $"demo-opening-{name}"));
    }
}
