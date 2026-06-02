using Dapper;
using Microsoft.Data.Sqlite;

namespace CS2Ultimod.Core.Database;

public sealed class SqliteDatabase : IDatabase, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteDatabase(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        _connection.Execute("PRAGMA journal_mode=WAL;");
        _connection.Execute("PRAGMA foreign_keys=ON;");
    }

    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await _gate.WaitAsync();
        try { return await _connection.ExecuteAsync(sql, parameters); }
        finally { _gate.Release(); }
    }

    public async Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null)
    {
        await _gate.WaitAsync();
        try { return await _connection.QueryFirstOrDefaultAsync<T>(sql, parameters); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await _gate.WaitAsync();
        try { return (await _connection.QueryAsync<T>(sql, parameters)).AsList(); }
        finally { _gate.Release(); }
    }

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            using var tx = await _connection.BeginTransactionAsync();
            try
            {
                var result = await work();
                await tx.CommitAsync();
                return result;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.CloseAsync();
        await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
