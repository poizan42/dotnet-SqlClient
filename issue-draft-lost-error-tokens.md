# Draft issue for dotnet/SqlClient (not posted)

Follows `.github/ISSUE_TEMPLATE/bug-report.md`. Suggested title:

**Server error tokens are lost when SQL Server aborts the connection before the driver has posted its read (bare transport error instead of the server's error)**

---

### Describe the bug

When SQL Server sends error tokens and then ends the session abortively, the driver often reports a bare transport-level error instead of the server's error. The tokens were sent and would have told the caller exactly what happened; they are lost on the client because the driver's read is not yet pending when they arrive.

The case we hit: a pooled connection whose deferred reset SQL Server refuses (the login database's collation changed while the connection sat in the pool). The server answers the first request after checkout with errors 4021 ("Resetting the connection results in a different state than the initial login. The login fails."), 18456 and 596 ("session is in the kill state"), and then disconnects the client with `DisconnectNamedPipe` (named pipes) or an RST (TCP). Both discard data the client has not read yet. Whether the application sees the 4021 or a transport error therefore depends on whether the driver had already posted its read.

The driver posts the read only when the reader first asks for data (`TdsParserStateObject.TryReadNetworkPacket` → `ReadSni` / `ReadSniSyncOverAsync`), after the request has been written. In a warm process that is 20-40 µs after the write and the tokens arrive fine. In a fresh process the reader code is still being JIT-compiled in that gap (we measured 1.6 ms with the native SNI, 42 ms with the managed SNI, more with MARS), and the tokens are gone by the time the read is posted. With MARS and async, in a fresh process, the error was lost in every run; the message is the one below.

```
Exception message (LocalDB, MARS, async, fresh process):
Microsoft.Data.SqlClient.SqlException (0x80131904): A transport-level error has occurred when receiving results from the server. (provider: Session Provider, error: 18 - Connection has been closed by peer)
Number=-1 Class=20

Exception message (TCP, SQL Server 2022, async, fresh process):
Microsoft.Data.SqlClient.SqlException (0x80131904): A transport-level error has occurred when receiving results from the server. (provider: TCP Provider, error: 0 - An existing connection was forcibly closed by the remote host.)
Number=10054 Class=20

Exception message + stack trace (LocalDB, no MARS, sync, fresh process; Microsoft.Data.SqlClient 7.1.1):
Microsoft.Data.SqlClient.SqlException (0x80131904): A transport-level error has occurred when receiving results from the server. (provider: Named Pipes Provider, error: 0 - No process is on the other end of the pipe.)
 ---> System.ComponentModel.Win32Exception (233): No process is on the other end of the pipe.
   at Microsoft.Data.SqlClient.SqlConnection.OnError(SqlException exception, Boolean breakConnection, Action`1 wrapCloseInAction)
   at Microsoft.Data.SqlClient.Connection.SqlConnectionInternal.OnError(SqlException exception, Boolean breakConnection, Action`1 wrapCloseInAction)
   at Microsoft.Data.SqlClient.TdsParser.ThrowExceptionAndWarning(TdsParserStateObject stateObj, SqlCommand command, Boolean callerHasConnectionLock, Boolean asyncClose)
   at Microsoft.Data.SqlClient.TdsParserStateObject.ThrowExceptionAndWarning(Boolean callerHasConnectionLock, Boolean asyncClose)
   at Microsoft.Data.SqlClient.TdsParserStateObject.ReadSniError(TdsParserStateObject stateObj, UInt32 error)
   at Microsoft.Data.SqlClient.TdsParserStateObject.ReadSniSyncOverAsync()
   at Microsoft.Data.SqlClient.TdsParserStateObject.TryReadNetworkPacket()
   at Microsoft.Data.SqlClient.TdsParserStateObject.TryPrepareBuffer()
   at Microsoft.Data.SqlClient.TdsParserStateObject.TryReadByte(Byte& value)
   at Microsoft.Data.SqlClient.TdsParser.TryRun(RunBehavior runBehavior, SqlCommand cmdHandler, SqlDataReader dataStream, BulkCopySimpleResultSet bulkCopyHandler, TdsParserStateObject stateObj, Boolean& dataReady)
   at Microsoft.Data.SqlClient.SqlDataReader.TryConsumeMetaData()
   at Microsoft.Data.SqlClient.SqlDataReader.get_MetaData()
   at Microsoft.Data.SqlClient.SqlCommand.FinishExecuteReader(SqlDataReader ds, RunBehavior runBehavior, String resetOptionsString, Boolean isInternal, Boolean forDescribeParameterEncryption, Boolean shouldCacheForAlwaysEncrypted)
   at Microsoft.Data.SqlClient.SqlCommand.RunExecuteReaderTds(CommandBehavior cmdBehavior, RunBehavior runBehavior, Boolean returnStream, Boolean isAsync, Int32 timeout, Task& task, Boolean asyncWrite, Boolean isRetry, SqlDataReader ds, Boolean describeParameterEncryptionRequest)
   at Microsoft.Data.SqlClient.SqlCommand.RunExecuteReader(CommandBehavior cmdBehavior, RunBehavior runBehavior, Boolean returnStream, TaskCompletionSource`1 completion, Int32 timeout, Task& executeTask, Boolean& usedCache, Boolean asyncWrite, Boolean isRetry, String method)
   at Microsoft.Data.SqlClient.SqlCommand.RunExecuteReader(CommandBehavior cmdBehavior, RunBehavior runBehavior, Boolean returnStream, String method)
   at Microsoft.Data.SqlClient.SqlCommand.ExecuteScalar()
ClientConnectionId:4818c58a-d149-46e2-935c-3f826eafa636
Error Number:233,State:0,Class:20

What the same request gets in a warm process (or whenever the read was already pending):
Microsoft.Data.SqlClient.SqlException (0x80131904): Resetting the connection results in a different state than the initial login. The login fails.
Login failed for user '...'. Reason: ... (18456)
Cannot continue the execution because the session is in the kill state. (596)
Number=4021 Class=16
```

### To reproduce

Complete program (`net10.0`, `Microsoft.Data.SqlClient` 7.1.1; also reproduces on current `main`; project at [repro/IssueRepro](https://github.com/poizan42/dotnet-SqlClient/tree/notes/pooled-reset-investigation/repro/IssueRepro)). Run it as a **fresh process** each time; it uses LocalDB by default, or pass a server name such as `tcp:.\MyInstance`. The collation change is just a convenient way to make SQL Server refuse a pooled connection's reset; the bug is in what happens to the server's answer afterwards.

```c#
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
```

Observed, fresh process each run:

| Server | Mode | Result |
|---|---|---|
| The listing above (MARS, async), LocalDB or TCP | fresh process | `Number=-1 Class=20` "Session Provider, error: 18 - Connection has been closed by peer", every run (5/5 when preparing this report) |
| LocalDB, no MARS, sync | fresh process | 233 "No process is on the other end of the pipe" in 2 of 3 runs, 4021 otherwise |
| TCP (SQL Server 2022), no MARS, async | fresh process | 10054 "forcibly closed by the remote host", every run |
| Any of the above | warm process (run a few pooled queries first, or loop the second step) | **4021** with the server's message, every time (121/121 in our runs) |

Three experiments that pin down the cause (done on a build of `main` with tracing and delay injection added to `TdsParserStateObject`: commit [fdc0222b4](https://github.com/poizan42/dotnet-SqlClient/commit/fdc0222b4) on [prototype/pooled-reset-recovery](https://github.com/poizan42/dotnet-SqlClient/tree/prototype/pooled-reset-recovery) in my fork; the repro programs and a longer write-up are on [notes/pooled-reset-investigation](https://github.com/poizan42/dotnet-SqlClient/tree/notes/pooled-reset-investigation)):

1. **Trace of the lost case** (TCP, async, fresh process). The write returns at t=0; `ReadSni` posts the read 1.65 ms later; `SNIReadAsync` returns 10054 immediately; no packet is ever received. Warm, the read is posted 0.04 ms after the write, pends, and completes 0.7 ms later with a 602-byte packet whose first token is `0xAA` (ERROR 4021).
2. **Injected delay.** Sleeping 5 ms before posting each read, in a warm process, loses the tokens in every run (6/6) on named pipes and TCP, sync and async, MARS on and off. Without the delay the same process gets the 4021 every time. So this is purely "was a read pending when the tokens arrived".
3. **Standalone Win32 check**, no SQL Server ([repro/UnreadDataTest](https://github.com/poizan42/dotnet-SqlClient/tree/notes/pooled-reset-investigation/repro/UnreadDataTest)): a server writes 100 bytes and tears down; the client reads 1-50 ms later with raw `ReadFile`/`recv`. `DisconnectNamedPipe` → client gets error 233 and the bytes are gone; `CloseHandle` → client reads the 100 bytes, then 109. TCP RST (linger 0) → 10054, bytes gone; FIN → 100 bytes, then EOF. The error codes the driver reports (233, 10054) show SQL Server uses the two discarding forms.

### Expected behavior

The application should receive the server's error (here 4021 + 18456 + 596) rather than a transport-level error, whenever the server actually sent it. The server's final message is the only thing that tells a caller *why* a session was killed; a bare transport error is also classified and retried differently (it is not `IsTransient`, and in this particular case it hides that nothing of the request executed).

Suggested fix: have a receive pending before the server can respond, so the tokens are delivered to it before the disconnect is processed. Two places it could live:

* In the SNI layer, keeping one receive outstanding per physical connection and buffering it (closes the window completely, and preserves the server's last tokens in every abortive-close scenario). The managed SNI (`SniTcpHandle.ReceiveAsync`, `SniNpHandle.ReceiveAsync`, `SniMarsConnection`) is in this repo; the native SNI would need the same.
* In the parser: post the read before the request's last packet is written, and have `TryReadNetworkPacket`/`ReadSni` adopt the already-pending read. I have a change doing this for the first request after a pooled checkout, with a simulated-server test, on [dev/poizan42/early-read-on-connection-reset](https://github.com/poizan42/dotnet-SqlClient/tree/dev/poizan42/early-read-on-connection-reset) (PR to follow): every lost case above then reports the 4021, including fresh-process MARS + async on LocalDB (13/13) and the 5 ms-delay cases (6/6 each). Cost per pooled checkout: within noise for async callers, +10-30 µs for synchronous ones. Posting the read *after* the write was not enough: in a fresh process the first execution of the posting code itself took ~1.5 ms of JIT, longer than the server's answer-plus-abort.

The server side could also avoid this (`FlushFileBuffers` before `DisconnectNamedPipe`, or a graceful close), but that is outside this repo and does not help existing servers.

### Further technical details

Microsoft.Data.SqlClient version: 7.1.1 (NuGet), and `main` at 192c1b8b5 built locally
.NET target: .NET 10.0 (10.0.402 SDK)
SQL Server version: SQL Server 2022 Standard 16.0.1200.5 (TCP); SQL Server 2016 LocalDB 13.0.7037 (named pipes)
Operating system: Windows 11 Enterprise 10.0.26200

**Additional context**

* Native SNI (default on Windows) and the managed SNI (`Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows`) both lose the tokens; managed is worse in a fresh process (42 ms to post its first async read; the error then surfaces as "TCP Provider, error: 35 - An internal exception was caught").
* With the native SNI, MARS does not keep a read pending on the physical connection either: with the injected delay it loses the tokens the same way, as "Session Provider, error: 18".
* The managed SNI **with MARS** is the exception, and the evidence that a pending receive is the fix: its SMUX receive pump (`SniMarsConnection`, re-armed receives, see #4680) keeps a read outstanding, and with the same 5 ms injected delay the 4021 survives 8/8 on both transports, where the native SNI loses it 6/6. What remains on that path is a fresh-process LocalDB failure reported as `Number=0 Class=20`: the packet *was* received, but the parser had already been broken by the physical connection's teardown before the session parsed it, which is #4969.
* Not tested on Linux. Linux TCP hands queued data to `recv` before reporting the RST, so the TCP form may not reproduce there; the named-pipe form is Windows-only anyway.
* The pooled-connection reset being refused in the first place (and nothing of the user's request executing when it is) is a separate problem we intend to file separately; this issue is only about the server's answer being lost. The two interact: a transparent recovery for the refused reset can only act on the 4021, which this bug hides.
* Related: #4969 (suspected bug 1 there). That issue is about tokens that *were* received being
  discarded on the client: with MARS, another session observes the teardown first,
  `SqlConnection.OnError` breaks the parser, and `TdsParser.TryRun` then returns `Done` for the
  command whose error token is sitting in the buffer. This issue is about the tokens never
  reaching the process at all, with or without MARS, sync or async. They stack: in our MARS
  traces, once the early read did deliver the 4021 packet, the physical connection's receive
  error (`0xFFFFFFFF`/10054 on the SMUX physical state object) could still arrive before the
  session parsed its packet, and the result was again the transport error. A fix for this issue
  makes #4969's ordering problem the remaining one on MARS; a fix for #4969 alone does not help
  when the packet was discarded by the OS. The managed SNI + MARS measurements above show the two
  in isolation: that path never loses the packet, and only #4969's symptom remains.
* Hit in practice in an integration-test suite: the test process's first use of the pool after a setup script changed a database's collation; the retry in place for 4021 never saw it because the error arrived as the transport form.
