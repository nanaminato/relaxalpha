using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelaxAlpha.Infrastructure;

namespace RelaxAlpha.Tests;

/// <summary>
/// 共享内存 SQLite：保持一个连接存活，多个 DbContext 实例即可看到同一份已迁移的库。
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection keeper;

    private TestDatabase(SqliteConnection keeper, string connectionString)
    {
        this.keeper = keeper;
        Options = new DbContextOptionsBuilder<SqliteMarketDatabase>().UseSqlite(connectionString).Options;
    }

    public DbContextOptions<SqliteMarketDatabase> Options { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var database = new TestDatabase(keeper, connectionString);
        await using var db = database.Create();
        await db.Database.MigrateAsync();
        return database;
    }

    public SqliteMarketDatabase Create() => new(Options);

    public ValueTask DisposeAsync() => keeper.DisposeAsync();
}
