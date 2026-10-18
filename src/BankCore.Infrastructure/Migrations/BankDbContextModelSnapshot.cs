using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BankCore.Infrastructure.Migrations;

[DbContext(typeof(BankDbContext))]
public sealed class BankDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0");
        BankDbContext.ConfigureModel(modelBuilder);
    }
}
