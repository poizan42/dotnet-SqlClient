# SqlClient: two proposals from a pooled connection whose reset the server refuses

Background: an integration-test suite whose database setup failed intermittently on the test
process's first use of the connection pool.
Source analysed: `dotnet/SqlClient` `main` at `192c1b8b5` (2026-11-03).
All line numbers below refer to that commit (unmodified `main`). Paths are relative to
`src/Microsoft.Data.SqlClient/src/Microsoft/Data/`.

This branch (`notes/pooled-reset-investigation`, an orphan branch of the fork
https://github.com/poizan42/dotnet-SqlClient) holds the material behind the issue and the PR:

| File | What |
|---|---|
| `README.md` | this write-up |
| `issue-draft-lost-error-tokens.md` | the issue text for the lost-error-tokens problem (Proposal 1) |
| `pr-draft-early-read.md` | the PR description for branch `dev/poizan42/early-read-on-connection-reset` |
| `prototype-pooled-reset-recovery.patch` | the five prototype commits as one patch (also branch `prototype/pooled-reset-recovery`) |
| `repro/IssueRepro` | minimal repro of the lost error (NuGet 7.1.1, LocalDB or any server) |
| `repro/ResetRepro` | the fuller repro used throughout (sync/async, MARS, external `ALTER`, side-effect check, benches; env vars documented in `Program.cs`) |
| `repro/UnreadDataTest` | standalone check that `DisconnectNamedPipe` / TCP RST discard unread data while `CloseHandle` / FIN do not |
| `repro/EnvProbe/Program.cs` | reflection probe showing the ENV_COLLATION change after `ALTER DATABASE … COLLATE` (needs a project reference to the driver) |
| `repro/run-early.sh` | the runner script used for the result tables (local paths) |

Branches on the fork: `prototype/pooled-reset-recovery` (opt-in prototypes of both proposals plus
tracing; §3.4), `dev/poizan42/early-read-on-connection-reset` (the clean fix for Proposal 1 with
tests, for the PR).

## TL;DR: two independent problems, two independent proposals

What the test suite saw is two defects stacked on each other. They have different causes, live
in different layers of the driver, and should be filed and fixed separately (§3.0 has a draft of
each).

**Problem 1 (transport): the driver loses the server's final error when the server aborts the
connection.** After SQL Server refuses a reset it sends the error tokens (4021, 18456, 596 "kill
state") and then ends the session with `DisconnectNamedPipe` or a TCP RST. Both discard data the
client hasn't read yet (verified in isolation, §1.5; a closed handle or a FIN would not). The
driver posts its read only when the reader first asks for data, after the request was written:
20-40 µs later warm, milliseconds later in a cold process while the reader is JIT-compiled. So a
warm process always gets the 4021 (121/121), and a cold one often gets a bare transport error
instead: LocalDB + MARS + async + fresh process loses it 4/4, with the exact message the test suite saw
("Session Provider, error: 18"). A warm process with a 5 ms delay injected before each read loses
it 6/6 on both transports. **Workaround in the driver: have a read pending before the server can
answer** (in the SNI transport, or by posting the read before the request is written).
**Prototyped (1b) and verified:** with the read posted before the reset-carrying request goes
out, every case that lost the tokens now gets the 4021: 13/13 in the test suite's configuration,
and 6/6 or better in every other transport/mode/MARS/delay combination (§3.0). The proper fix is
server-side (flush, or close gracefully), outside this repo.

**Problem 2 (pool): the pool vends a session the server will refuse to reset.** The reset is only
a bit on the next user's first request (§1.1-1.3); nothing validates it at checkout, and no
existing mechanism recovers (§2). Two verified facts make a fix safe and cheap: when the reset is
refused **nothing in the request executes** (an `INSERT` landed 0/14 times across both
transports), and the session that ran `ALTER DATABASE … COLLATE` **receives an ENV_COLLATION
ENVCHANGE**, so the driver knows that session can't be reset. **Fix: (a) prevention: don't pool a
session whose login database's collation changed since login; (b) recovery: when the first
request after checkout comes back with 4021 and no reset acknowledgement, replace the physical
connection and re-run the command once**, following the existing internal re-run in
`SqlCommand.RunExecuteReader`. Neither adds a round trip to the normal path. **Both are
prototyped and verified;** with (b) and Proposal 1 together, every repro case succeeds
transparently on the same `SqlConnection`, with the command executed exactly once (§3.0).

**How they relate.** Fix 2(b) only ever sees the 4021 if fix 1 is in place (verified: 2b alone
still loses a cold process's first reuse to the transport error, and recovers everything else);
with both, a refused reset is always recovered. Fix 2(a) alone already prevents the test suite's pattern (6/6 on both
transports, cold async included), because the stale session never reaches the pool. Fix 1 alone
turns the test suite's unretried transport error into the 4021 its test code already retried.

**Rejected:** resetting at checkout with a no-op batch (Design A below) covers everything and is
prototyped (8/8), but costs a round trip on every pooled `Open` (~127→217 µs LocalDB, ~228→385 µs
local TCP), which upstream won't take for an edge case. Kept only as a reference.

No existing dotnet/SqlClient issue or PR covers either problem (§4).

---

## 1. Where the reset happens today

### 1.1 Return to pool: only a flag is set

* `SqlClient/Connection/SqlConnectionInternal.cs:2207` `Deactivate()` → `:2243 ResetConnection()`
  runs when the connection goes back to the pool, unless the connection is doomed.
* `SqlClient/Connection/SqlConnectionInternal.cs:4234` `ResetConnection()` calls
  `_parser.PrepareResetConnection(...)` when `Connection Reset=true`, the default
  (`_fResetConnection` is read from the connection string at `:446`). It then rewinds
  `CurrentDatabase`/`_currentLanguage` on the client only. No I/O happens.
* `SqlClient/Parser/TdsParser.cs:2333` `PrepareResetConnection()` only sets
  `_fResetConnection = true` (declared at `TdsParser.cs:84`) and `_fPreserveTransaction`.
* `SqlClient/ConnectionPool/WaitHandleDbConnectionPool.cs:1463` and
  `ChannelDbConnectionPool.cs:385` also call `ResetConnection()` when a connection moves from the
  transacted pool back to general circulation (`PutObjectFromTransactedPool`).

### 1.2 Checkout: no reset, only a socket probe

* `WaitHandleDbConnectionPool.cs:967` `TryGetConnection` → `:1069 GetFromGeneralPool()` →
  `:1072` drops the connection if it is idle-expired or `!obj.IsConnectionAlive()`. It then calls
  `:1143 PrepareConnection` → `:1151-1168` `obj.ActivateConnection(...)`. If that throws, the
  `catch` calls `ReturnInternalConnection` (which destroys the connection if it is doomed) and
  **rethrows to the caller's `Open`**. Nothing retries.
* `ChannelDbConnectionPool.cs` (the v2 pool, `Switch.Microsoft.Data.SqlClient.UseConnectionPoolV2`)
  works the same way: `TryGetPooledConnectionInline` (`:1469`, `PrepareConnection` at `:1504`) and
  `GetInternalConnection` (`:1528`, liveness re-check at `:1624`, `PrepareConnection` at `:1636`).
  `PrepareConnection` (`:1775`) also catches, returns the connection and rethrows.
* `SqlClient/Connection/SqlConnectionInternal.cs:1201` `IsConnectionAlive` →
  `SqlClient/Parser/TdsParserStateObject.cs:4176`. It only does anything if the last successful I/O
  is older than `CheckConnectionWindow` (`:102`, 50,000 ticks = 5 ms). Then it calls SNI
  `CheckConnection()`, a socket-level probe. It never talks TDS.
* `SqlConnectionInternal.cs:2150` `Activate()`. New on `main` (PR #4330, fixes #96, opt-in
  `EnableTransactionIsolationLevelReset`): if the previous user changed the isolation level, it
  runs `ResetSessionIsolationLevel` (`:4264`). That is a real batch sent **during checkout**, so
  it already carries the pending reset bit. On failure it dooms the connection and throws, so the
  caller's `Open` fails. That is the same bug surfacing at a different point.

### 1.3 First request: the bit goes out in the header

* `SqlClient/Parser/TdsParserStateObject.cs:3034` `WritePacket` → `_parser.CheckResetConnection(this)`
  ("HAS SIDE EFFECTS").
* `TdsParser.cs:1879-1975` `CheckResetConnection` ORs `ST_RESET_CONNECTION` (or
  `…_PRESERVE_TRANSACTION`) into `_outBuff[1]` of the first packet (`:1921-1930`).
  * **Non-MARS:** `_fResetConnection = false` is set immediately (`:1932-1935`), i.e. when the bit
    is *written*, not when the server acknowledges it.
  * **MARS:** a `_resetConnectionEvent` serialises sessions. `_fResetConnectionSent` /
    `_fResetEventOwned` are cleared in `TdsParserStateObject.cs:985-1008`. "RESET SUCCEEDED" there
    only means the read callback had no transport error.
* The server acknowledges a reset with `ENV_SPRESETCONNECTIONACK`
  (`SqlConnectionInternal.cs:1290`, it clears `_currentSessionData`). Nothing on the client waits
  for or checks that acknowledgement.

### 1.4 What happens when the server refuses

From the server log: `18456 state 50` "Current collation did not match the database's collation
during connection reset", then `18056 state 50`. The server kills the session. The batch in the
same request is **not executed** (verified, §3.4). The client then sees one of two things:

* The token stream arrives: `4021/16`, `18456/14`, `596/21` ("session is in the kill state"),
  `0/20`. Class ≥ 20 breaks the connection (`TdsParser.ThrowExceptionAndWarning` →
  `SqlConnection.OnError(breakConnection: true)`). The `SqlException.Number` is **4021** and the
  `SqlConnection` is left `Closed`.
* The pipe closes before the client reads: `ReadSniSyncOverAsync` → `ReadSniError` gives a
  transport error, class 20. On LocalDB over named pipes it was `233` ("No process is on the
  other end of the pipe"). Over TCP it was `10054` ("connection reset by peer"). The test suite saw "Session Provider, error: 18 – Connection has been
  closed by peer".

In my repro the transport form came on **the first reuse in a process**, and the 4021 form on all
later ones. That matches the observation in the test suite. It is timing-dependent:

"Warm" below means the process first ran three normal pooled queries through the same sync or
async code path.

| Transport | MARS | Mode | Cold process: first reuse | Warm: all reuses |
|---|---|---|---|---|
| Named pipes (LocalDB 2016) | off | sync | 233 in 2 of 3 runs | 4021 in 45/45 |
| Named pipes (LocalDB 2016) | off | async | 233 in 2 of 3 runs | 4021 in 24/24 |
| Named pipes (LocalDB 2016) | **on** | sync | 4021 in 4/4 runs | 4021 in 24/24 |
| Named pipes (LocalDB 2016) | **on** | **async** | **`-1` "Session Provider, error: 18" in 4/4 runs** | 4021 in 24/24 |
| TCP (SQL Server 2022 Standard) | off | sync | 4021 in 7/7 runs | 4021 in 20/20 |
| TCP (same) | off | async | 10054 in 4/4 runs | 4021 in 32/32 |
| TCP (same) | on | async | `-1` "Session Provider, error: 18" in 1 of 4 reuses per run | — |

The test suite's configuration is the bold row: LocalDB, MARS on, async, a fresh test process.
There the loss is deterministic on the first reuse, and the message is the test suite's exact
one: `Number=-1 Class=20` "A transport-level error has occurred when receiving results from the
server. (provider: Session Provider, error: 18 - Connection has been closed by peer)".

### 1.5 Why the tokens are lost (verified in the client)

I instrumented the local build (`TdsParserStateObject`, commit `fdc0222b4`, env-var gated) to log
each write and read with its SNI result and the received packet, and to optionally sleep before
posting a read. Over TCP, native SNI, cold async (iteration 0 of the repro):

```
452.121 ms  write returned sniError=0 sync=False status=0x09 (reset bit=True) bytes=126
453.769 ms  posting async read (SniReadAsync)            <- 1.65 ms after the write
454.233 ms  async read posted sniError=10054              <- fails at once; no packet received
```

Warm async, same request: the read is posted 0.04 ms after the write, pends, and completes 0.7 ms
later with a 602-byte packet whose first token is `0xAA` (ERROR): the 4021. The server sends the
tokens in both cases; the only difference is whether a read was pending when they arrived.

Controlled experiment: a warm process with a 5 ms sleep injected before every read:

| | Named pipes (LocalDB) | TCP (SQL Server 2022) |
|---|---|---|
| sync, 5 ms delay | 233 in 6/6 | 10054 in 6/6 |
| async, 5 ms delay | 233 in 6/6 | 10054 in 6/6 |
| MARS, async, 5 ms delay | `-1` "Session Provider, error: 18" in 6/6 | `-1` same in 6/6 |
| async, no delay (control) | 4021 in 6/6 | 4021 in 6/6 |

Whether Windows really discards unread data depends on *how* the server end goes away, so I checked
it in isolation, without SQL Server (`repro/UnreadDataTest` on this branch: a server
writes 100 bytes, tears down, and the client reads 1-50 ms later with raw `ReadFile`/`recv`):

| Server action | Client's first read | Second read |
|---|---|---|
| Named pipe, `DisconnectNamedPipe` | **error 233** `ERROR_PIPE_NOT_CONNECTED`, data gone | 233 |
| Named pipe, `CloseHandle` | 100 bytes | error 109 `ERROR_BROKEN_PIPE` |
| TCP, abortive close (RST, linger 0) | **error 10054** `WSAECONNRESET`, data gone | 10054 |
| TCP, graceful close (FIN) | 100 bytes | 0 bytes (EOF) |

So a closed handle or a FIN lets the client drain the buffer first, as one would expect, and only
`DisconnectNamedPipe` and an RST discard it. The error codes SqlClient reports on the lost reset
response are exactly 233 on LocalDB and 10054 on TCP, so SQL Server ends a killed session with
`DisconnectNamedPipe` (documented to discard unread client data unless the server calls
`FlushFileBuffers` first, which would block it until the client reads) and with an RST. A read
that is already pending when the data arrives gets it; a read posted after the teardown gets only
the error. Both
paths post the read from the reader's first `TryReadNetworkPacket`, after the request was written.
Warm, that is ~20 µs (sync) to ~40 µs (async) after the write. Cold, the JIT work in between is
what loses the race: cold sync lost on named pipes in 2 of 3 processes (0 of 7 over TCP, where the
server's answer takes longer to arrive), and cold async lost 1.6 ms (native SNI) to 42 ms (managed
SNI, `UseManagedNetworkingOnWindows`) after the write, with MARS adding its own work on top. That
is why "the fatal form came on a process's first use of the pool".

With the native SNI, MARS does not keep a read pending on the physical connection: with the
delay, MARS loses the tokens just like non-MARS (6/6). The managed SNI without MARS loses them too
(cold async: callback error 1, "TCP Provider, error: 35"; with the delay, `Number=0 Class=20` 4/4).
**The managed SNI with MARS is the exception:** its SMUX receive pump (`SniMarsConnection`,
re-armed receives, PR #4680) keeps a read outstanding, and with the same 5 ms delay the 4021
survives 8/8 on LocalDB and TCP. That is in-repo precedent for the SNI-layer form of Proposal 1
(§3.0, option 1a). What that path still shows in a fresh process on LocalDB is `Number=0
Class=20`: the packet arrived but the parser had already been broken by the physical connection's
teardown, which is dotnet/SqlClient#4969 (filed 2026-11-03, "async MARS command can lose its own
fatal error ... after another session tears down the connection", managed SNI). The two problems
stack; see the issue draft for the cross-reference.

Not tested: Linux. Linux TCP hands queued data to `recv` before reporting the RST, so the 4021 may
survive there; WSL with .NET 10 is available for that test but needs a SQL login on the TCP
instance (Integrated Security doesn't work from WSL).

## 2. Why the existing mechanisms don't cover it

| Mechanism | Where | Why it misses this | Basis |
|---|---|---|---|
| Pool dead-connection check | `WaitHandleDbConnectionPool.cs:1072`, `ChannelDbConnectionPool.cs:1308` → `TdsParserStateObject.cs:4176` | It is a socket probe and the session is healthy. The server only objects when the reset arrives, which is after checkout. | Verified (code). The repro shows the same physical connection is vended (`sameConn=True`). |
| Connection resiliency / idle connection recovery (`ConnectRetryCount`, default 1) | `SqlCommand.Reader.cs:1292` / `SqlCommand.NonQuery.cs:790` → `SqlConnection.cs:1841 ValidateAndReconnect` → `ReconnectAsync` `:1751` | It runs **before** the request is sent, and only if `_sessionRecoveryAcknowledged` **and** `!ValidateSNIConnection()` (`:1857-1860`, `TdsParserStateObject.cs:946`), i.e. the socket is *already* dead. Here the socket is fine until the request goes out. After a mid-request failure nothing reconnects; by design, because it can't know whether the request ran. | Verified (code). Repro fails with default `ConnectRetryCount=1`. |
| Transient-fault retry, `SqlRetryLogicProvider` | `Reliability/SqlConfigurableRetryFactory.cs:48` (defaults), `:117` `NoneRetryProvider`; `SqlCommand.cs:724`, `SqlConnection.cs:157` | (a) Off by default for both commands and connections. (b) 4021 is not in the default transient list, and the transport error numbers (233, Session Provider 18) aren't either. (c) **Even when you opt in and list 4021/233**, the retry re-executes on the same `SqlConnection`, which the class-20 error has `Closed`. Result: `InvalidOperationException: ExecuteScalar requires an open and available Connection`. A *connection* retry provider only wraps `Open`, which succeeded. | Verified: (a)/(b) from code, (c) by repro (§3.3). |
| `SqlException.IsTransient` | — | `False` for both forms. Related open issue #4665 covers the transport form. | Verified (repro prints it). |
| `ResetSessionIsolationLevel` on checkout (new, opt-in) | `SqlConnectionInternal.cs:2174-2180`, `:4264` | When it runs, it carries the reset and turns the failure into an `Open` failure. The pool still doesn't retry (`PrepareConnection` rethrows). | Verified (code). Not exercised in the repro. |
| Doomed-connection handling after the failure | `Deactivate` / `DestroyObject` | Works: the next `Open` gets a fresh connection (`next:OK` in every run). That's why retrying "the whole thing" at app level (Open + command) works, and why the test suite's 4021 retry hid the problem. | Verified (repro). |

Inferred, not verified: other causes of a refused reset should behave the same way, since the
server re-validates the login context during reset. Examples: the login database dropped, offline
or renamed, or access to it revoked. The repro covers only the collation case. Design A is generic
and should cover the others; Design C only covers the collation case.

## 3. Fix designs

### 3.0 The two proposals

Ground rule for both: nothing is added to the normal path. The deferred reset exists precisely to
avoid a round trip per pooled `Open`, and the only precedent for a checkout-time batch (#4330)
pays it just for connections already flagged dirty, behind an opt-in switch.

#### Proposal 1: don't lose the server's final error on an abortive disconnect

*Layer:* SNI transport / `TdsParserStateObject`. *Independent of pooling and of collation.*

*Root cause (§1.5):* the server writes its last tokens and then aborts (`DisconnectNamedPipe`,
TCP RST). Windows discards unread data on both. The driver's read is posted from the reader's
first `TryReadNetworkPacket` (`TdsParserStateObject.cs:3394`, `ReadSni` `:3899`,
`ReadSniSyncOverAsync` `:3508`), after `SNIWritePacket` (`:3048`) returned, so whether the tokens
survive depends on how fast that code runs. Any server message followed by an abort can be lost
this way; the refused reset is just the case we have a repro for.

*Change, two options:*

* **(1a) SNI layer: keep one receive outstanding per physical connection**, buffering what arrives
  until the parser asks. Closes the window completely. The managed SNI is in-repo
  (`ManagedSni/SniTcpHandle.netcore.cs:1053 ReceiveAsync`, `SniNpHandle.netcore.cs:230`,
  `SniMarsConnection.netcore.cs:130`); the native SNI, the Windows default, is the closed
  `Microsoft.Data.SqlClient.SNI` package, so that half is a request to its owners. MARS doesn't
  do this today (verified: with the delay it loses the tokens too).
* **(1b) Parser: post the read as soon as the request's last packet is written**, in
  `SNIWritePacket`/`WritePacket(HARDFLUSH)`, for sync and async alike, and have
  `TryReadNetworkPacket` adopt the already-pending read. Narrows the window to the warm-process
  size (tens of µs) regardless of JIT state. Needs care with `_networkPacketTaskSource`, the
  snapshot/replay machinery (`_snapshot`, `_asyncReadWithoutSnapshot`) and MARS's
  `_resetConnectionEvent`, because a packet may now arrive before the reader attaches.

*Prototype of (1b), commit `7edaedd53` on the branch, opt-in `SQLCLIENT_REPRO_EARLY_READ=1`:*
`TdsParserStateObject.ExecuteFlush` posts the read (`ReadSni`) **before** writing the final
packet when the request carries the deferred reset (`_parser._fResetConnection &&
!_fResetConnectionSent`). Posting *after* the write was tried first and is not enough: in a cold
process the first execution of the posting code itself took 1.5 ms of JIT, and the server's
answer-plus-abort arrives within that. Adoption: `ReadSni` hands a pending early read to the
caller's completion source instead of posting a second read (`SqlCommand.Reader.cs:398` and
`TryReadNetworkPacket` both end up there); a synchronous `TdsParser.TryRun` waits for the early
read before touching the buffer, so the SNI callback thread and the parser thread never work on
it at once (the first version lacked this and corrupted a parse on fast named-pipe responses); a
read the SNI refuses to post (seen once as `0xFFFFFFFF` with MARS over named pipes when posted
after the write) falls back silently; a completed early read nobody adopted is discarded.

Results, every case that previously produced a transport error now produces the 4021:

| Case (early read on) | Result |
|---|---|
| LocalDB + MARS + cold async (the test suite's case) | 4021 in 13/13 |
| LocalDB cold async, cold sync (no MARS) | 4021 in 6/6, 12/12 |
| LocalDB warm, 5 ms delay before each read: async / sync / MARS async | 4021 in 6/6 each (+4/4) |
| TCP cold async, MARS on / off | 4021 in 12/12, 6/6 |
| TCP warm, 5 ms delay: async / sync / MARS async | 4021 in 6/6 each |
| External `ALTER` (other connection), cold async | 4021 in 4/4 |
| Refused `INSERT` executed | 0/10 (unchanged: nothing runs) |
| Happy path, 20,000 × pooled `Open` + `SELECT 1` | LocalDB 125 → 145 µs; TCP 205 → 240 µs |

The happy-path cost is the prototype's per-reuse `TaskCompletionSource`, timer and `Task.Wait`
(sync) machinery; it applies once per pooled checkout, not per command. A product version would
wait inside the SNI for the sync case and skip the timer, or do (1a) in the SNI layer, where an
always-pending receive costs nothing per request. Not covered by the prototype: multi-packet
requests where the reset bit is on an earlier packet than the one flushed last (harmless, the
read is still posted before the final packet), `SqlBulkCopy`, and long async soak runs.

*Not covered:* a response that was genuinely lost (network failure). Nothing client-side can help
there. The proper fix for this problem is on the server: `FlushFileBuffers` before
`DisconnectNamedPipe`, or a graceful close. That belongs in SQL Server feedback, not this repo.

*Tests:* the simulated TDS server in `tests/tools/TDS` answering a request with ERROR tokens and
then closing the socket with linger 0; assert the client surfaces the ERROR, not a transport error,
with the reader deliberately delayed. Named-pipe variant against LocalDB with the reset repro
(§3.4): with a cold process and MARS + async, assert 4021 instead of `-1`.

*Draft issue text:*

> **Title:** Server error tokens are lost when the server aborts the connection before the client
> has posted its read
>
> When SQL Server ends a session abortively right after sending error tokens (seen with a refused
> pooled-connection reset: 4021, 18456, 596, then `DisconnectNamedPipe` on named pipes or an RST
> on TCP), Windows discards unread data, and the driver reports a bare transport error (233 /
> 10054 / "Session Provider, error: 18", Class 20) instead of the server's error. The driver only
> posts its read from the reader's first `TryReadNetworkPacket`, after the request was written;
> in a cold process (JIT), with async and MARS, that is milliseconds later and the tokens are
> gone. A warm process gets the tokens every time; a warm process with a 5 ms delay injected
> before each read loses them every time (repro and trace attached). Proposal: keep a receive
> pending before the response can arrive, either in the SNI layer (one outstanding receive per
> physical connection) or by posting the read immediately after the request's last packet is
> written. This also preserves the server's final message in other abort scenarios.

#### Proposal 2: the pool should not vend, and the driver should recover, a session whose reset the server refuses

*Layer:* `SqlConnectionInternal` / pool / `SqlCommand`. *Independent of Proposal 1, but 2(b) only
sees the 4021 reliably once Proposal 1 is in.*

*Root cause (§1.1-1.4, §2):* the reset is a flag set on return and a header bit on the next
user's first request; nothing validates it at checkout; a refusal fails that user's command,
closes their `SqlConnection`, and no existing mechanism retries.

*Change:*

* **(2a) Prevention (Design C, prototyped):** `OnEnvChange` records the login database's collation
  at login and the latest `ENV_COLLATION` since (`SqlConnectionInternal.cs:1238`); `Deactivate`
  (`:2207`) dooms the connection instead of pooling it when the session is back in its login
  database and the collation differs. Zero cost. Verified 6/6 on LocalDB and TCP, sync and async.
  Misses an `ALTER` run from another connection (that session never sees the ENVCHANGE).
* **(2b) Recovery (Design B-narrow, designed):** in `CheckResetConnection` record on the state
  object that the request carried the reset bit; in `TdsParser.TryRun`, if ERROR 4021 arrives
  before `ENV_SPRESETCONNECTIONACK` and before any other token, mark `ResetRefused`. Verified: in
  that state the server executed nothing (an `INSERT` landed 0/14 times). On that marker,
  `SqlCommand.RunExecuteReader` (`SqlCommand.Reader.cs` ≈`:1180-1255`, which already re-runs once
  with `isRetry: true` for `TCE_CONVERSION_ERROR_CLIENT_RETRY` and the enclave retry) suppresses
  the state change that would close the outer connection (as resiliency does with
  `_suppressStateChangeForReconnection`), replaces the inner connection via
  `SqlConnectionFactory.TryGetConnection(forceNewConnection: true)` → pool `ReplaceConnection`
  (`WaitHandleDbConnectionPool.cs:1177`), and re-runs once with the remaining timeout. Guards: not
  when `isRetry`, not with a local or enlisted transaction, not with other active MARS sessions.
  Covers every cause of a refused reset, not just collation. Also: when a checkout batch that
  already happens fails (#4330's isolation-level reset carries the pending reset), the pool should
  discard the connection and vend another instead of failing `Open` (prototyped for the WaitHandle
  pool). And `SqlException.IsTransient` should be true for a 4021 on a reset-carrying request
  (next to #4665).

*Prototype of (2b), commit `4c2c4162f` on the branch, opt-in `SQLCLIENT_REPRO_RESET_RETRY=1`:*

* *Detection:* `TdsParserStateObject._lastWriteCarriedReset` is cleared when packet 1 of a request
  is built and set by `CheckResetConnection` when the reset bit goes out; `ENV_SPRESETCONNECTIONACK`
  clears it. In `TryRun`'s `SQLERROR` case, `4021 && _lastWriteCarriedReset` →
  `SqlConnection.MarkResetRefused()`, which also sets `_suppressStateChangeForReconnection` so
  the class-20 break that follows closes the connection without a user-visible state change.
* *Replacement:* `SqlConnection.TryReplaceConnectionAfterRefusedReset()` re-opens with
  `TryOpen(null, forceNewConnection: State == Open)`. Normally the break has already closed the
  connection, so this is a plain pooled open; the doomed inner was destroyed on close. Mirrors
  `ReconnectAsync` minus the session-recovery data (a refused reset leaves nothing to recover).
* *Re-run, sync:* `RunExecuteReader`'s non-encryption branch catches `SqlException` `when
  (!isRetry && !isAsync && TryRecoverFromRefusedReset())` and calls itself once with
  `isRetry: true` and the remaining timeout, the same shape as the Always Encrypted retry above.
* *Re-run, async:* `InternalEndExecuteReader`, which every `End…`/`…Async` path goes through and
  where the 4021 is first observed (`CompleteAsyncExecuteReader` consuming the metadata), catches
  the `SqlException` when the marker is set, replaces the connection and re-runs once,
  synchronously, with the first attempt's `CommandBehavior`. A product version would re-issue the
  request asynchronously. (An earlier version reused the Always Encrypted internal-end-and-retry
  machinery; §3.6 explains why that was wrong.)
* *Guards:* no re-run when the command has a `SqlTransaction` or an ambient
  `System.Transactions.Transaction`; the marker is cleared at the start of every non-retry
  execution so a stale one can't fire later. Not hooked: `ExecuteNonQuery` (goes through
  `RunExecuteNonQueryTds`), `BeginTransaction`, `ChangeDatabase`, `SqlBulkCopy`.

Results, both prototypes on (`OK(sameConn=True)` means the stale pooled session was vended, the
command still succeeded on the same `SqlConnection` object, and the pooled `INSERT` ran exactly once):

| Case (1b + 2b on) | Result |
|---|---|
| LocalDB + MARS + cold async (the test suite's case) | OK 7/7; `INSERT` ran exactly once 4/4 |
| LocalDB cold sync / warm sync / warm async | OK 3/3, 5/5, 4/4; `INSERT` once 8/8 |
| TCP cold async MARS on / off, cold sync | OK 3/3, 3/3, 3/3; `INSERT` once 4/4 |
| External `ALTER` (other connection), cold async | OK 4/4 |
| Delayed reader (5 ms), MARS async | OK 4/4 |
| 2b alone (no early read), LocalDB + MARS cold async | first reuse still `-1` transport error, later ones OK: 2b needs Proposal 1 for the cold case |
| 2b alone, warm sync | OK 3/3 |
| `ExecuteNonQuery` (not hooked) | 4021 as today, 3/3 |
| Happy path, 20,000 × pooled `Open` + `SELECT 1`, two rounds | LocalDB 84-102 µs off → 98-108 (1b) → 114-117 (1b+2b); TCP 182-192 → 193 → 188-200. 1b ≈ +5-15 µs per checkout; 2b not measurable on TCP. |

*Not covered:* a refused reset whose 4021 was lost in transport (Proposal 1's problem) still
fails as today: no tokens, so no safe replay. Also the unhooked execution paths above, and the
repo's own test suites, which were not run.

*Tests:* §3.5. Simulated TDS server answering a reset-carrying request with 4021/18456/596 +
close; assert `Open` + `ExecuteScalar` succeed, the user's batch reached the server once, a new
physical connection was made; transaction case fails as today. Manual test against SQL Server:
the §3.4 repro, direct and external `ALTER`.

*Draft issue text:*

> **Title:** Pooled connection whose reset the server refuses fails the user's first command
> instead of being replaced
>
> After `ALTER DATABASE … COLLATE` on a pooled connection, the next checkout of that connection
> fails on the user's first command with 4021 "Resetting the connection results in a different
> state than the initial login" (+18456 state 50, 596) and the `SqlConnection` is closed, although
> the user's command never executed (verified with an `INSERT`) and the next `Open` works. The
> pool doesn't validate the deferred reset, connection resiliency only acts before a request is
> sent, and `SqlRetryLogicProvider` can't re-run on the closed connection. Proposal: (a) don't
> return a session to the pool when its login database's collation changed since login (the
> session receives an ENV_COLLATION ENVCHANGE, so the driver knows); (b) when the first request
> after checkout is answered with 4021 before any reset acknowledgement, replace the physical
> connection and re-run the command once, as `RunExecuteReader` already does for the Always
> Encrypted retry; (c) mark such a 4021 `IsTransient`. No extra round trip on the normal path.
> Repro attached (LocalDB and SQL Server 2022, sync and async).

The design notes below (A, B, C) are the detailed material behind these two proposals. Design A
(reset at checkout) is kept as a reference only.

### Design A (complete, but costs a round trip): reset at checkout, replace on refusal

**Idea:** when a reused pooled connection is checked out and a reset is pending, send the reset now
with a no-op batch instead of piggybacking it on the caller's first request. If that fails for any
reason, doom the connection and have the pool vend another one: an idle one, or a new physical
connection. Nothing of the caller's has been sent, so re-acquiring is always safe, whatever the
failure looked like (4021, transport error, timeout).

**Where:**

* `SqlConnectionInternal.Activate` (`:2150`), right after the isolation-level block: if the switch
  is on and `_parser._fResetConnection` (i.e. reused) and `EnlistedTransaction is null` and
  `!IsTransactionRoot`, call `FlushPendingReset(timeout)`. This mirrors `ResetSessionIsolationLevel`:
  the same timeout budget (the remaining `Open` time) and the same doom-and-rethrow. On failure it
  sets a marker `DbConnectionInternal.PooledResetRefused = true`.
  * If `ResetSessionIsolationLevel` runs anyway, its batch already carries the reset. Mark its
    failure the same way and skip the extra batch.
  * Note: an **empty** batch gets no response from SQL Server (the client waited for its 15 s
    timeout in my first attempt). The batch must contain a statement. The prototype uses
    `DECLARE @_ INT;`. `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` would also work and would
    fold in #96's fix.
* Both pools: around `PrepareConnection`, `catch when (obj.PooledResetRefused && retriesLeft > 0 && !timeout.IsExpired)`
  → the connection has already been returned doomed and destroyed → get another one.
  * `WaitHandleDbConnectionPool.TryGetConnection` (`:967`, `PrepareConnection` at `:1143`). Its
    pending-open thread (`WaitForPendingOpen`, `:779`/`:821`) calls the same private method, so
    async `OpenAsync` is covered too. Verified.
  * `ChannelDbConnectionPool.TryGetPooledConnectionInline` (`:1504`) and `GetInternalConnection`
    (`:1636`). Not in the prototype.
  * Bound the retries (the prototype uses 3 plus the `Open` timeout). A sensible production rule:
    keep trying idle connections until the timeout, but if one refusal happened, prefer creating
    a new connection (as `ReplaceConnection` does), so you don't drain a pool full of stale
    sessions one round trip at a time.
  * Alternatively, put the loop once in `SqlConnectionFactory.TryGetConnection` (`:384`, it already
    has a retry loop for the pool-shutdown race). But async opens complete inside the pool's own
    task, so the pool level is the more natural place.

**Opt-in / trigger.** One extra round trip per pooled `Open`. Measured on LocalDB/named pipes,
20,000 × pooled `Open` + `SELECT 1`: **~127 µs → ~217 µs**. Over local TCP (SQL Server 2022):
**~228 µs → ~385 µs**. Over a network it is +1 RTT
(0.2-2 ms). That is exactly the cost the deferred-reset design exists to avoid, so it can't be the
default. Options:

* A connection-string keyword, e.g. `Pool Reset Mode = Deferred | OnCheckout`, or an AppContext
  switch. `OnCheckout` also gives "validate on borrow" semantics that people sometimes want for
  flaky networks (Npgsql/HikariCP-style "test on borrow").
* **Targeted:** do the eager reset only for connections marked *suspect* when they were returned.
  The marks would be: the Design C collation signal, `_isolationLevelDirty` (already an eager
  batch), or, after a refused reset was seen in this pool, all idle connections in it until a
  clean checkout. That keeps the default path free and still covers most real cases.

**Trade-offs and edge cases:**

* *Transactions:* connections with an enlisted or delegated transaction (transacted pool,
  `ST_RESET_CONNECTION_PRESERVE_TRANSACTION`) are excluded. A replacement can't take over the
  server-side transaction. If the reset is refused there, the transaction is lost anyway, and
  failing as today is correct.
* *MARS:* the reset is per physical connection, guarded by `_resetConnectionEvent`. At checkout
  only the physical state object is in use, so sending on `_physicalStateObj` is fine (as
  `ResetSessionIsolationLevel` already does). Needs a MARS test.
* *Async:* the prototype sends the batch synchronously inside `Activate`, as
  `ResetSessionIsolationLevel` does. For the WaitHandle pool, `OpenAsync` pending opens run on the
  pool's own thread. For the Channel pool's async path a truly async send would be nicer (later).
* *Timeouts:* use the remaining `Open` budget. A timeout during the flush is also "doom and try
  another".
* *Pool counters/metrics:* the prototype relies on `ReturnInternalConnection` → `DestroyObject`
  for the soft/hard gauges. The retry path calls `Metrics.SoftConnectRequest()` again, which pairs
  with the soft disconnect from the failed one. Needs a check against the metrics tests.
* *Session recovery data* stays consistent: a new physical connection logs in fresh and the reset
  state equals a fresh login.

### Design B: replay the caller's first request after a positively identified refusal

**Idea:** keep the deferred reset (no extra round trip). Detect "this request carried the reset
and the server refused it" and, if so, replace the inner connection and re-send the request once.

* Detection must be **positive**. The request carried the reset bit (record it on the state object
  in `CheckResetConnection`), no `ENV_SPRESETCONNECTIONACK` was seen, and the token stream contains
  4021, or 18456 followed by a class ≥ 20 kill, *before any other token*. In that case the server
  has refused the request as a whole. The batch did not run, verified with an `INSERT`, so a replay
  is safe.
* **The transport-error form can't be handled by replay.** "Connection closed before any token"
  looks the same as "network died after the server executed the batch". Replaying could run a
  non-idempotent command twice. Connection resiliency refuses to reconnect mid-request for the
  same reason. The transport form is precisely the one the test suite's retry didn't catch, which is
  why §3.0 item 5 (keep a read pending so the tokens are not lost) matters: it converts that form
  into the 4021 form.
* **Where:** every first-request path would need the hook. That includes `RunExecuteReaderTds`
  (`SqlCommand.Reader.cs:1273`), NonQuery/Scalar/Xml/Batch, `BeginTransaction`
  (`TdsExecuteTransactionManagerRequest`), `ChangeDatabase`, `SqlBulkCopy`, Always Encrypted
  describe-parameter calls, sync and async, single- and multi-packet requests, plus MARS. The
  replacement itself exists: `SqlConnectionFactory.TryGetConnection(forceNewConnection: true)` →
  pool `ReplaceConnection` (`WaitHandleDbConnectionPool.cs:1177`), as resiliency's `ReconnectAsync`
  uses. But re-sending a request is far more invasive than Design A, and the parameters/streams of
  some requests can't be re-sent at all.
* A minimal, low-risk slice of B is worth doing anyway: **classify** it. Make
  `SqlException.IsTransient` true for 4021 + 18456 on a request that carried an unacknowledged
  reset, add 4021 to the default transient list, and document that the `SqlConnection` is closed
  afterwards. That helps app-level retry (Open + command) but is not transparent.

**Recommendation:** see §3.0. Propose B-narrow, limited to the definite-4021 case and modelled on
the existing `isRetry` re-run in `RunExecuteReader`, together with Design C and the
classification. Do not replay on transport errors.

Implementation sketch for B-narrow:

* **Detect.** In `CheckResetConnection`, record on the state object that this request carried the
  reset. When the response is processed (`TdsParser.TryRun`), set a `ResetRefused` marker if
  ERROR 4021 arrives before `ENV_SPRESETCONNECTIONACK` and before any other token.
* **Keep the outer connection open.** On the class-20 break, do what resiliency does
  (`_suppressStateChangeForReconnection`) so the outer `SqlConnection` doesn't go `Closed` when the
  marker is set.
* **Replace and re-run.** In `RunExecuteReader`'s catch, guarded by `!isRetry` plus no local or
  enlisted transaction plus no other active MARS session: replace the inner connection with
  `forceNewConnection: true`, the same path `ReconnectAsync` uses. A refused reset leaves the
  session equivalent to a fresh login, so no session-recovery data is needed. Then re-run once with
  the remaining timeout.
* **Async paths** need the same treatment in the `Begin…`/`…Async` continuations. That is where
  most of the work is.

### Design C: don't pool a session whose login database's collation changed

* Verified: after `ALTER DATABASE … COLLATE` the server sends `ENV_COLLATION` to that session
  (parser `_defaultCollation` LCID 1033 → 1030, `_currentSessionData._collation` updated while
  `_initialCollation` stays 1033). `SqlConnectionInternal.OnEnvChange` `ENV_COLLATION` is handled
  at `:1238`.
* Change: remember the collation from login (`ENV_COLLATION` while `!_fConnectionOpen`), and keep
  the latest one. In `Deactivate` (`:2207`), before `ResetConnection()`: if the session is in its
  login database (`CurrentDatabase` equals `_originalDatabase`, case-insensitively, which errs
  towards not pooling) and the current collation differs from the login collation → doom it, so it
  is destroyed instead of pooled.
  * This doesn't depend on `ENVCHANGE` ordering (checked at return). It doesn't need session
    recovery to be negotiated: the prototype tracks its own fields because `_currentSessionData`
    is null when `ConnectRetryCount=0`.
* Limits (verified for the first): it misses an `ALTER` run from **another** connection while the
  pooled session sits in a different database. `ALTER DATABASE … COLLATE` needs exclusive access to
  the database, so this happens when pooled sessions `USE`d elsewhere. It also misses
  non-collation causes. Cost: none on the happy path.

### 3.4 Prototype and results

Branch `prototype/pooled-reset-recovery` (`c270452c1`), 3 files, +128/−2:
`ProviderBase/DbConnectionInternal.cs` (`PooledResetRefused`),
`SqlClient/Connection/SqlConnectionInternal.cs` (`FlushPendingReset`, the collation tracking, the
`Deactivate` doom), `SqlClient/ConnectionPool/WaitHandleDbConnectionPool.cs` (catch-and-retry
around `PrepareConnection`). Switches (prototype names, not for upstream as is):

* `Switch.Microsoft.Data.SqlClient.Prototype.ValidatePooledResetOnCheckout` (Design A)
* `Switch.Microsoft.Data.SqlClient.Prototype.DoomOnLoginDatabaseCollationChange` (Design C)

Repro: `repro/ResetRepro` on this branch (NuGet 7.1.1); the same `Program.cs` was also built
against the local clone (a `ResetReproLocal` project with a `ProjectReference` to the driver). Each iteration creates a throwaway database with
`Latin1_General_CI_AS`, opens a pooled connection, alters the collation to `Danish_Norwegian_CI_AS`
(or with `REPRO_EXTERNAL=1`: `USE master` on the pooled session, then `ALTER` from a separate
unpooled connection), closes the connection, reopens it and runs a query, then drops the database.
Env vars: `REPRO_ASYNC`, `REPRO_SIDE` (the query also `INSERT`s into a side database),
`REPRO_RETRY` (command retry provider with 4021/233 as transient), `REPRO_SWITCHES`,
`REPRO_BENCH=n`.

| Scenario | Result |
|---|---|
| 7.1.1, sync / async | 15/15 and 8/8 fail on reuse: first per process `233/20` (transport), the rest `4021/16 + 18456/14 + 596/21 + 0/20`. Connection `Closed`, next `Open` OK. |
| 7.1.1, `INSERT` in the refused request | **0 of 8 rows inserted**: the refused request never runs. |
| 7.1.1, `SqlRetryLogicProvider` with 4021, 233, … | All fail with `InvalidOperationException` (connection closed). |
| `main`, no switches | 6/6 fail, same as 7.1.1. |
| `main` + Design C | Direct `ALTER`: 6/6 OK, a fresh connection is vended. External `ALTER`: 6/6 still fail. |
| `main` + Design A, sync / async / external / external+async / with side `INSERT` | **8/8 OK each.** This includes the transport-error case on the first reuse. Each `INSERT` ran exactly once (8 rows for 8 iterations). |
| `main` + A + C | 8/8 OK. |
| Cost of A on the happy path (20,000 × pooled `Open` + `SELECT 1`, LocalDB/named pipes) | ~127 µs → ~217 µs per iteration (3 runs each). |
| **TCP, SQL Server 2022 Standard** (`REPRO_SERVER=tcp:.\SQL2026STANDARD`), 7.1.1 | Same failure. The refused `INSERT` ran 0/6 times. Forms per §1.4 (10054 on cold async first reuse, otherwise 4021). |
| TCP, `main` + Design A: sync / cold async / external+async / side `INSERT` | **6/6 OK each.** Each `INSERT` ran exactly once. |
| TCP, `main` + Design C: cold async / external | 6/6 OK / 6/6 still fail (as predicted). |
| TCP, cost of A | ~228 µs → ~385 µs per pooled `Open` + `SELECT 1` (2 runs each). |

Second commit `fdc0222b4`: the wire trace and read-delay injection used in §1.5
(`SQLCLIENT_REPRO_TRACE=1`, `SQLCLIENT_REPRO_READ_DELAY_MS=n`), in `TdsParserStateObject.cs` and
`TdsParserStateObject.Multiplexer.cs`. Diagnostics only; no behaviour change when unset.

Third commit `7edaedd53`: Proposal 1 (1b), the read posted before a reset-carrying request
(`SQLCLIENT_REPRO_EARLY_READ=1`). Fourth commit `4c2c4162f`: Proposal 2 (2b), replace and re-run
once on a definite 4021 (`SQLCLIENT_REPRO_RESET_RETRY=1`). Results for both are in §3.0.

Fifth commit `7712d3024`: fixes from running the repo's test suites (§3.6).

Not done: the Channel (v2) pool, transaction scenarios, `ExecuteNonQuery`/`BeginTransaction`/
bulk-copy paths for 2b, long async soak runs, and the manual tests that need an elevated process
or SQL authentication (§3.6).
With the switches and env vars off, the only always-on changes are the collation bookkeeping in
`OnEnvChange` and the `_lastWriteCarriedReset` flag maintenance in the parser.

### 3.5 How to test upstream

* **Unit tests on the simulated TDS server** (`src/Microsoft.Data.SqlClient/tests/tools/TDS`).
  `QueryEngine.cs:50-53` already sees `request.IsResetConnectionRequestSet` and counts resets.
  Add a test server, modelled on `TransientTdsErrorTdsServer`, that answers a request carrying the
  reset bit with ERROR 4021 + 18456 + 596 (class 21) and then closes the socket, with a variant that
  closes *without* tokens. Template: `tests/UnitTests/SimulatedServerTests/IsolationLevelResetTests.cs`
  (the checkout-time batch from #4330) and `PoolClearDuringOpenTests.cs`. Assert for both pools
  (`UseConnectionPoolV2` on and off), sync and async:
  * with the feature on, `Open` + `ExecuteScalar` succeed and a new physical connection was made;
  * the user's batch reached the server exactly once;
  * with a transaction enlisted, it fails as today;
  * the retry stops at the bound or the timeout;
  * pool metrics stay balanced (see `WaitHandleDbConnectionPoolBudgetTest.cs`).
* **Manual tests against real SQL Server** (`tests/ManualTests/SQL/ConnectionPoolTest/ConnectionPoolTest.cs`):
  the repro above (create a database, `ALTER DATABASE … COLLATE` on a pooled connection, close,
  reopen, query, drop), the external-`ALTER` variant, and a sanity case (no `ALTER`) checking that
  the reset still happens (`sp_reset_connection`-observable state such as a temp table is gone).
* **Design C unit test:** the simulated server sends `ENV_COLLATION` mid-session; assert the
  connection isn't returned to the idle pool (pool count / new `ClientConnectionId`).

### 3.6 The repo's own test suites against the prototype branch

Run with `build.proj` targets on net10.0, Debug configuration (so `Debug.Assert`s are live),
against SQL Server 2022 Standard over TCP and LocalDB over named pipes, with Northwind created
on both. "On" = `SQLCLIENT_REPRO_EARLY_READ=1 SQLCLIENT_REPRO_RESET_RETRY=1`.

| Suite | Switches | Result | Failures |
|---|---|---|---|
| Unit (2048) | off | 2039 passed, 0 failed, 9 skipped | — |
| Unit | on | 2039 passed, 0 failed, 9 skipped | — |
| Functional (1370) | off, on, and **`main`** | 1339 passed, 16 failed, 15 skipped, identical in all three | 14 × `CultureNotFoundException` in culture-specific string comparisons, 2 × certificate store needs administrator rights. Environment. |
| Manual set 2 (671) | on | 627 passed, 2 failed, 42 skipped | 2 × bulk copy "locale id 1030 vs 1033": Northwind inherited the instance's Danish collation. Environment. |
| Manual set 3 (513) | on | 453 passed, 6 failed, 54 skipped | 4 × `SqlCredentialTest` (instance has no SQL authentication), 2 × TVP sort order (Danish date format). Environment. |
| Manual set 1 (50) | on | 14 passed, 31 failed, 5 skipped | 31 × `CertificateTestWithTdsServer`: "User needs to have elevated access". Environment. |

Two prototype defects the suites found, both fixed in commit `7712d3024`:

* The first version of 2b's async re-run went through the Always Encrypted internal-end-and-retry
  path (`TriggerInternalEndAndRetryIfNecessary` / `CreateLocalCompletionTask`). That path sets
  `_internalEndExecuteInitiated` and never clears it when the internal end fails without a retry,
  so the user's own `End…` call then takes the "already ended" branch and throws
  `InvalidOperationException` ("Invalid command sent to ExecuteXmlReader", "EndExecuteXmlReader
  cannot be called more than once") instead of the server's `SqlException`. 61 retry-logic tests
  in manual set 2 failed. The re-run now lives in `InternalEndExecuteReader`, where every
  `End…`/`…Async` path observes the 4021, after `CompleteAsyncExecuteReader`'s finally has cleaned
  up the first attempt; set 2 then passes apart from the two locale tests. The same tests also
  showed that both re-run catches must only *test* the marker in the exception filter and replace
  the connection in the catch body, because filters run before inner `finally` blocks.
* `PostEarlyRead` hit `ReadSni`'s `Debug.Assert` when a finished async operation had left a
  completed `_networkPacketTaskSource` behind (`UpdateLockedTable`); it now clears such a leftover.

Also fixed: the wire tracing had been placed in `TdsParserStateObject.Multiplexer.cs`, which the
functional test project compiles on its own, so that suite did not build until the trace moved.

## 4. Existing issues / PRs in dotnet/SqlClient (read-only search)

Searches for `4021`, `18056`, "different state than the initial login", collation + reset/pool,
"connection reset" pool, `sp_reset_connection`, "kill state" / 596, `ClearPool` + `ALTER DATABASE`,
and validate/ping-on-checkout. **None covers a refused pooled reset.** The `4021` hits are PR
numbers, not the error. Related:

* [dotnet/SqlClient#4665](https://github.com/dotnet/SqlClient/issues/4665) (open):
  `SqlException.IsTransient` is false for a transport error on a stale pooled connection. Same
  family (pooled connection fails on first use, retry on a fresh one succeeds). It asks for
  classification, not transparent recovery.
* [dotnet/SqlClient#4330](https://github.com/dotnet/SqlClient/pull/4330) (merged, fixes #96) and
  [#4335](https://github.com/dotnet/SqlClient/pull/4335): the session isolation level reset at
  checkout. This is the precedent for a checkout-time round trip, opt-in via an AppContext switch,
  and the natural place to attach Design A.
* [dotnet/SqlClient#3667](https://github.com/dotnet/SqlClient/issues/3667) (open): connection pool
  reliability testing setup.
* [dotnet/SqlClient#4130](https://github.com/dotnet/SqlClient/pull/4130): database-context
  reconnection tests and diagnostics (session recovery restoring the wrong database, #4108).
  Adjacent area, different problem.
* [dotnet/SqlClient#3019](https://github.com/dotnet/SqlClient/pull/3019) /
  [#4557](https://github.com/dotnet/SqlClient/pull/4557) / issue #4001: preserving transactions on
  pooled reset. Relevant to the transaction exclusion in Design A.

If you file this upstream, I'd suggest framing it as "a pooled connection whose reset the server
refuses fails the caller's first command, although nothing ran". Propose: don't pool sessions whose
login database's collation changed; re-run once on a definite 4021; classify it as transient.
Cite the repro and #4665/#4330, and mention reset-on-checkout only as the opt-in alternative.

## 5. For the test suite regardless of upstream

The test suite's own fix, `SqlConnection.ClearPool(connection)` after the collation script, is the right local one.
It destroys the session that ran the `ALTER` (and any other idle session in that pool) before
anyone reuses it. The test code's 4021 retry can be removed once that's in, since it only hid the
symptom and never caught the transport form.
