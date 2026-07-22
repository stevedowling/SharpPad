using System.Data;
using Dapper;

namespace SharpPad.Runtime;

/// <summary>
/// Database access for scripts that declare a #connection directive.
/// The project generator emits a module initializer that calls Init(),
/// then scripts use Connection / Query / Execute directly
/// (via `global using static SharpPad.Runtime.Db;`).
/// </summary>
public static class Db
{
    private static string? _provider;
    private static string? _connectionString;
    private static IDbConnection? _shared;

    public static void Init(string provider, string connectionString)
    {
        _provider = provider;
        _connectionString = connectionString;
    }

    /// <summary>Shared open connection, created on first use.</summary>
    public static IDbConnection Connection
    {
        get
        {
            if (_shared is null || _shared.State != ConnectionState.Open)
            {
                _shared = CreateConnection();
                _shared.Open();
            }
            return _shared;
        }
    }

    /// <summary>New unopened connection if you want to manage your own.</summary>
    public static IDbConnection CreateConnection()
    {
        if (_provider is null || _connectionString is null)
            throw new InvalidOperationException(
                "No database connection configured. Add a `#connection \"name\"` directive to the script.");
        return _provider switch
        {
            "sqlite" => new Microsoft.Data.Sqlite.SqliteConnection(_connectionString),
            "postgres" => new Npgsql.NpgsqlConnection(_connectionString),
            "sqlserver" => new Microsoft.Data.SqlClient.SqlConnection(_connectionString),
            "mysql" => new MySqlConnector.MySqlConnection(_connectionString),
            _ => throw new NotSupportedException($"Unknown provider '{_provider}'.")
        };
    }

    public static IEnumerable<dynamic> Query(string sql, object? param = null) =>
        Connection.Query(sql, param);

    public static IEnumerable<T> Query<T>(string sql, object? param = null) =>
        Connection.Query<T>(sql, param);

    public static T QuerySingle<T>(string sql, object? param = null) =>
        Connection.QuerySingle<T>(sql, param);

    public static int Execute(string sql, object? param = null) =>
        Connection.Execute(sql, param);
}
