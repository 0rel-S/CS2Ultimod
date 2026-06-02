namespace CS2Ultimod.Core.Database;

public interface IDatabase
{
    Task<int> ExecuteAsync(string sql, object? parameters = null);
    Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null);
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null);
    Task<T> InTransactionAsync<T>(Func<Task<T>> work);
}
