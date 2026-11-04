// Does ALTER DATABASE ... COLLATE send an ENV_COLLATION ENVCHANGE to the session that ran it?
using System.Reflection;
using Microsoft.Data.SqlClient;

const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
string master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;Pooling=false";
string db = "EnvProbe_" + Guid.NewGuid().ToString("N")[..10];
Exec(master, $"CREATE DATABASE [{db}] COLLATE Latin1_General_CI_AS");
try
{
    using var c = new SqlConnection($@"Server=(localdb)\MSSQLLocalDB;Database={db};Integrated Security=true");
    c.Open();
    Dump(c, "after open");
    using (var cmd = new SqlCommand($"ALTER DATABASE [{db}] COLLATE Danish_Norwegian_CI_AS", c)) cmd.ExecuteNonQuery();
    Dump(c, "after ALTER DATABASE COLLATE");
    using (var cmd = new SqlCommand("USE master", c)) cmd.ExecuteNonQuery();
    Dump(c, "after USE master");
    using (var cmd = new SqlCommand($"USE [{db}]", c)) cmd.ExecuteNonQuery();
    Dump(c, "after USE db");
}
finally
{
    SqlConnection.ClearAllPools();
    Exec(master, $"DROP DATABASE [{db}]");
}

static void Dump(SqlConnection c, string label)
{
    object inner = typeof(SqlConnection).GetProperty("InnerConnection", F).GetValue(c);
    object parser = inner.GetType().GetField("_parser", F).GetValue(inner);
    object defColl = parser.GetType().GetField("_defaultCollation", F).GetValue(parser);
    object sd = inner.GetType().GetField("_currentSessionData", F).GetValue(inner);
    string T(object coll) => coll is null ? "null" : (string)coll.GetType().GetMethod("TraceString", F).Invoke(coll, null);
    string cur = sd is null ? "(no session data)" : T(sd.GetType().GetField("_collation", F).GetValue(sd));
    string init = sd is null ? "" : T(sd.GetType().GetField("_initialCollation", F).GetValue(sd));
    Console.WriteLine($"{label,-30} parser._defaultCollation={T(defColl)}  session._collation={cur}  _initialCollation={init}");
}

static void Exec(string cs, string sql)
{
    using var c = new SqlConnection(cs);
    c.Open();
    using var cmd = new SqlCommand(sql, c);
    cmd.ExecuteNonQuery();
}
