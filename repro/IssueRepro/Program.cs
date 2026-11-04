using Microsoft.Data.SqlClient;

// Server: any SQL Server; LocalDB shows it most reliably. MARS + async in a fresh process loses the
// error every time; sync or a warmed-up process gets the real error (4021) instead.
string server = args.Length > 0 ? args[0] : @"(localdb)\MSSQLLocalDB";
string db = "LostTokens_" + Guid.NewGuid().ToString("N")[..8];
string master = $"Server={server};Database=master;Integrated Security=true;TrustServerCertificate=true;Pooling=false";
string pooled = $"Server={server};Database={db};Integrated Security=true;TrustServerCertificate=true;MultipleActiveResultSets=true";

Exec(master, $"CREATE DATABASE [{db}] COLLATE Latin1_General_CI_AS");
try
{
    // 1. Use a pooled connection to change the collation of its own login database, then return it to the pool.
    using (var c = new SqlConnection(pooled))
    {
        c.Open();
        ExecOn(c, $"ALTER DATABASE [{db}] COLLATE Danish_Norwegian_CI_AS");
    }

    // 2. Reuse it. The first request carries the connection reset; SQL Server refuses the reset
    //    (18456 state 50 / 18056 in the server log) and answers with 4021, 18456, 596, then aborts.
    using (var c = new SqlConnection(pooled))
    {
        await c.OpenAsync();
        using var cmd = new SqlCommand("SELECT 1", c);
        try
        {
            await cmd.ExecuteScalarAsync();
            Console.WriteLine("unexpected: succeeded");
        }
        catch (SqlException e)
        {
            Console.WriteLine($"Number={e.Number} Class={e.Class}: {e.Message}");
        }
    }
}
finally
{
    SqlConnection.ClearAllPools();
    Exec(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]");
}

static void Exec(string cs, string sql) { using var c = new SqlConnection(cs); c.Open(); ExecOn(c, sql); }
static void ExecOn(SqlConnection c, string sql) { using var cmd = new SqlCommand(sql, c); cmd.ExecuteNonQuery(); }
