// Repro: ALTER DATABASE ... COLLATE on a pooled connection, close it, reopen (same physical
// connection from the pool), run a command -> server refuses the deferred reset.
using System.Data;
using Microsoft.Data.SqlClient;

// REPRO_SWITCHES=name1,name2 turns on AppContext switches (the prototype's are opt-in).
foreach (string sw in (Environment.GetEnvironmentVariable("REPRO_SWITCHES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    AppContext.SetSwitch(sw.Trim(), true);
// REPRO_EXTERNAL=1: the pooled session moves to master and the ALTER runs on another, unpooled
// connection, so the pooled session never sees the collation ENVCHANGE.
bool external = Environment.GetEnvironmentVariable("REPRO_EXTERNAL") == "1";

// REPRO_SERVER overrides the server, e.g. "tcp:.\SQL2026STANDARD" to test over TCP.
string srv = Environment.GetEnvironmentVariable("REPRO_SERVER") ?? @"(localdb)\MSSQLLocalDB";

if (Environment.GetEnvironmentVariable("REPRO_BENCH") is string benchN)
{
    // Cost of checkout on the happy path: pooled Open + one tiny query, n times.
    string bcs = $@"Server={srv};TrustServerCertificate=true;Database=master;Integrated Security=true;Application Name=ResetReproBench";
    for (int pass = 0; pass < 2; pass++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = int.Parse(benchN);
        bool benchAsync = Environment.GetEnvironmentVariable("REPRO_BENCH_ASYNC") == "1";
        for (int k = 0; k < n; k++)
        {
            using var c = new SqlConnection(bcs);
            if (benchAsync) await c.OpenAsync(); else c.Open();
            using var cmd = new SqlCommand("SELECT 1", c);
            if (benchAsync) await cmd.ExecuteScalarAsync(); else cmd.ExecuteScalar();
        }
        if (pass == 1) Console.WriteLine($"bench{(benchAsync ? " async" : "")}: {n} x (pooled Open + SELECT 1): {sw.Elapsed.TotalMilliseconds / n * 1000:F0} us each");
    }
    return;
}

if (Environment.GetEnvironmentVariable("REPRO_WARMUP") == "1")
{
    // Warm the pooled Open/execute/read paths (JIT) before the first refused reset.
    using var w = new SqlConnection($@"Server={srv};TrustServerCertificate=true;Database=master;Integrated Security=true;Application Name=ResetReproWarmup");
    bool warmAsync = Environment.GetEnvironmentVariable("REPRO_ASYNC") == "1";
    for (int k = 0; k < 3; k++)
    {
        if (warmAsync) await w.OpenAsync(); else w.Open();
        using (var wc = new SqlCommand("SELECT 1", w)) { if (warmAsync) await wc.ExecuteScalarAsync(); else wc.ExecuteScalar(); }
        w.Close();
    }
}

int iterations = args.Length > 0 ? int.Parse(args[0]) : 10;
string extra = args.Length > 1 ? args[1] : "";
bool useAsync = Environment.GetEnvironmentVariable("REPRO_ASYNC") == "1";
string master = $@"Server={srv};TrustServerCertificate=true;Database=master;Integrated Security=true;Pooling=false";
Console.WriteLine($"SqlClient {typeof(SqlConnection).Assembly.GetName().Version} ({System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(SqlConnection).Assembly.Location).ProductVersion}); async={useAsync}; external={external}; extra='{extra}'; switches='{Environment.GetEnvironmentVariable("REPRO_SWITCHES")}'");

bool side = Environment.GetEnvironmentVariable("REPRO_SIDE") == "1";
bool retry = Environment.GetEnvironmentVariable("REPRO_RETRY") == "1";
string sideDb = "ResetReproSide_" + Guid.NewGuid().ToString("N")[..8];
if (side) Exec(master, $"CREATE DATABASE [{sideDb}]; EXEC('USE [{sideDb}]; CREATE TABLE dbo.Marks(i int)')");
using (var t = new SqlConnection(master))
{
    t.Open();
    using var tc = new SqlCommand("SELECT CONCAT(net_transport, ' ', @@VERSION) FROM sys.dm_exec_connections WHERE session_id = @@SPID", t);
    Console.WriteLine($"server: {srv}; transport: {FirstLine((string)tc.ExecuteScalar())}");
}
var tally = new Dictionary<string, int>();
for (int i = 0; i < iterations; i++)
{
    string db = "ResetRepro_" + Guid.NewGuid().ToString("N")[..12];
    Exec(master, $"CREATE DATABASE [{db}] COLLATE Latin1_General_CI_AS");
    string cs = $@"Server={srv};TrustServerCertificate=true;Database={db};Integrated Security=true;Application Name=ResetRepro{i};{extra}";
    string outcome;
    try
    {
        Guid first;
        using (var c = new SqlConnection(cs))
        {
            c.Open();
            first = c.ClientConnectionId;
            using var cmd = new SqlCommand(external ? "USE master" : $"ALTER DATABASE [{db}] COLLATE Danish_Norwegian_CI_AS", c);
            cmd.ExecuteNonQuery();
        } // back to the pool, reset flag pending
        if (external) Exec(master, $"ALTER DATABASE [{db}] COLLATE Danish_Norwegian_CI_AS");

        using (var c = new SqlConnection(cs))
        {
            if (useAsync) await c.OpenAsync(); else c.Open();
            bool same = c.ClientConnectionId == first;
            try
            {
                using var cmd = new SqlCommand(side
                    ? $"INSERT INTO [{sideDb}].dbo.Marks(i) VALUES ({i}); SELECT DATABASEPROPERTYEX(DB_NAME(),'Collation')"
                    : "SELECT DATABASEPROPERTYEX(DB_NAME(),'Collation')", c);
                if (retry)
                    cmd.RetryLogicProvider = SqlConfigurableRetryFactory.CreateFixedRetryProvider(new SqlRetryLogicOption
                    {
                        NumberOfTries = 3,
                        DeltaTime = TimeSpan.FromMilliseconds(50),
                        TransientErrors = new[] { 4021, 233, 0, -1, 10054, 109 },
                    });
                object r;
                if (Environment.GetEnvironmentVariable("REPRO_NONQUERY") == "1")
                {
                    // ExecuteNonQuery goes through RunExecuteNonQueryTds, which the 2b prototype does not hook.
                    r = useAsync ? await cmd.ExecuteNonQueryAsync() : cmd.ExecuteNonQuery();
                    r = "nonquery rows=" + r;
                }
                else
                {
                    r = useAsync ? await cmd.ExecuteScalarAsync() : cmd.ExecuteScalar();
                }
                outcome = $"OK(sameConn={same}) {r}";
            }
            catch (AggregateException ae) when (ae.InnerException is SqlException)
            {
                outcome = $"FAIL-AGG(sameConn={same}) {FirstLine(ae.Message)}";
            }
            catch (InvalidOperationException ioe)
            {
                outcome = $"FAIL-IOE(sameConn={same}) {ioe.Message}";
            }
            catch (SqlException e)
            {
                outcome = $"FAIL(sameConn={same}) Number={e.Number} Class={e.Class} State={c.State} IsTransient={e.IsTransient} errs=[{string.Join(",", e.Errors.Cast<SqlError>().Select(x => $"{x.Number}/{x.Class}"))}]";
                if (i == 0 || retry || e.Class >= 20) Console.WriteLine(e.GetType().Name + ": " + FirstLine(e.Message) + (e.InnerException is null ? "" : " / inner: " + e.InnerException.Message) + $" [Number={e.Number} State={e.State} Class={e.Class} Server={e.Server} Proc={e.Procedure}]");
            }
        }

        // Third use: does the pool hand out a working connection now?
        using (var c = new SqlConnection(cs))
        {
            c.Open();
            using var cmd = new SqlCommand("SELECT 1", c);
            try { cmd.ExecuteScalar(); outcome += " | next:OK"; }
            catch (SqlException e) { outcome += $" | next:FAIL {e.Number}"; }
        }
    }
    finally
    {
        SqlConnection.ClearAllPools();
        Exec(master, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]");
    }
    string key = System.Text.RegularExpressions.Regex.Replace(outcome, @"Danish_Norwegian_CI_AS|Latin1_General_CI_AS", m => m.Value);
    Console.WriteLine($"{i,3}: {outcome}");
    tally[key] = tally.GetValueOrDefault(key) + 1;
}
if (side)
{
    using var c = new SqlConnection(master); c.Open();
    using var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{sideDb}].dbo.Marks", c);
    Console.WriteLine($"side-effect rows inserted by the refused requests: {cmd.ExecuteScalar()} (of {iterations})");
    SqlConnection.ClearAllPools();
    Exec(master, $"DROP DATABASE [{sideDb}]");
}
Console.WriteLine("--- summary");
foreach (var kv in tally) Console.WriteLine($"{kv.Value,4} x {kv.Key}");

static string FirstLine(string s) => s.Split(Environment.NewLine)[0];

static void Exec(string cs, string sql)
{
    using var c = new SqlConnection(cs);
    c.Open();
    using var cmd = new SqlCommand(sql, c);
    cmd.ExecuteNonQuery();
}
