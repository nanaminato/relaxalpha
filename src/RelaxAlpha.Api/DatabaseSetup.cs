using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RelaxAlpha.Infrastructure;

namespace RelaxAlpha.Api;

public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteMarketDatabase>
{
    public SqliteMarketDatabase CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteMarketDatabase>().UseSqlite("Data Source=relaxalpha.db").Options);
}

public sealed class PostgresDesignFactory : IDesignTimeDbContextFactory<PostgresMarketDatabase>
{
    public PostgresMarketDatabase CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresMarketDatabase>()
            .UseNpgsql("Host=localhost;Database=relaxalpha").Options);
}
