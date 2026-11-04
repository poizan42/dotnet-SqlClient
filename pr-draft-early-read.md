# PR draft: dev/poizan42/early-read-on-connection-reset (not opened)

Branch: https://github.com/poizan42/dotnet-SqlClient/tree/dev/poizan42/early-read-on-connection-reset
(two commits on upstream `main` `c4441400b`: `fc2d0769f` the fix, `383e3c099` the tests). Open with:
https://github.com/poizan42/dotnet-SqlClient/pull/new/dev/poizan42/early-read-on-connection-reset

Suggested title: **Post the read before sending a request that carries the connection reset, so the server's error is not lost when it aborts the connection**

Follows `.github/pull_request_template.md`.

---

## Description

When SQL Server refuses to reset a pooled connection (for example because the login database's
collation changed while the connection sat in the pool), it answers the first request after
checkout with error tokens (4021 "Resetting the connection results in a different state than the
initial login", 18456, 596) and then ends the session abortively: `DisconnectNamedPipe` on named
pipes, an RST on TCP. On Windows both discard data the client has not read yet.

The driver posts its read only when the reader first asks for data
(`TdsParserStateObject.TryReadNetworkPacket` → `ReadSni` / `ReadSniSyncOverAsync`), after the
request was written. In a warm process that is tens of microseconds later and the tokens arrive.
In a fresh process the reader is still being JIT-compiled in that gap (measured 1.6 ms with the
native SNI, 42 ms with the managed SNI, more with MARS) and the tokens are gone by the time the
read is posted, so the caller gets a bare transport error (233, 10054, or "Session Provider,
error: 18 - Connection has been closed by peer", Class 20) instead of the server's error. With
MARS and async in a fresh process the error was lost in every run. The transport error is also
classified and retried differently from the real error, and in this case it hides that nothing
of the request executed.

This change makes `TdsParserStateObject.ExecuteFlush` post the read (`ReadSni`) before the final
packet of a request that carries the reset bit goes out, so a read is pending whenever the answer
arrives:

- `ReadSni` adopts a pending early read instead of posting a second one, handing its outcome to
  the caller's completion source. This covers both `SqlCommand`'s direct `ReadSni` call in the
  `Begin…` paths and `TryReadNetworkPacket`.
- A synchronous `TdsParser.TryRun` waits for the early read (`AdoptEarlyReadSync`) before it
  touches the buffer, so the SNI callback thread and the parser thread never work on the buffer
  at the same time. `TryReadNetworkPacket`'s sync branch does the same for readers that reach the
  network without going through `TryRun`.
- A read the SNI refuses to post falls back silently to the normal path.
- A completed early read that no reader adopted (data consumed without `TryRun`/`ReadSni`) is
  discarded before a new one is posted.

Posting the read *after* the write was tried first and is not enough: in a fresh process the
first execution of the posting code itself takes longer (~1.5 ms) than the server needs to answer
and abort. Before the write, JIT time does not matter because the server cannot answer yet.

Scope and cost: only requests that carry the deferred reset are affected, i.e. the first request
after a pooled checkout. Measured per pooled `Open` + `SELECT 1`, 20,000 iterations, loopback,
this branch vs. its base: no measurable change for async callers (−8 to +8 µs, within noise);
+10-30 µs for synchronous callers, which otherwise use the SNI's cheaper blocking read and now
pay for the async read machinery (timer, task source, pinned callback) on that one request.

No public API changes. No behaviour change when the server accepts the reset, other than the
timing of the read.

The managed SNI with MARS already behaves this way through its SMUX receive pump
(`SniMarsConnection`, #4680) and does not lose the tokens; this change gives the other paths
(native SNI with or without MARS, managed SNI without MARS) the same property at the parser level.
A follow-up could move it into the SNI layer as an always-outstanding receive per physical
connection, which would also preserve the server's final tokens in other abortive-close cases.

## Issues

Fixes #4994.

Related: #4969 (tokens that *were* received being discarded because another MARS session broke
the parser first). The two stack: this change makes sure the tokens reach the process; #4969 is
what remains on MARS once they do.

The underlying problem that the pool vends a session the server will refuse to reset, and that
the caller's command then fails although nothing executed, is a separate issue (to be filed
separately); this PR only stops the server's answer from being lost.

## Testing

New simulated-server tests, `tests/UnitTests/SimulatedServerTests/EarlyReadOnConnectionResetTests.cs`
(Debug configuration, like the other parser test seams):

- `ResetRefusingTdsServer` (new, `tests/tools/TDS/TDS.Servers`) answers any request that carries
  the reset-connection flag with error tokens 4021, 18456 and 596 and then closes the client
  socket with an RST, the way SQL Server ends a killed session. The abort is a new
  `ITDSServerSession.AbortConnectionAfterResponse` flag acted on by `TDSServerParser` after the
  response is written, with the socket close (linger 0) supplied by `TDSServerEndPointConnection`;
  `ConnectionAborted` lets a test sequence its next step after the abort.
- A new DEBUG-only seam, `TdsParserStateObject.s_beforeReadPost`, runs before a read is posted on
  demand (not for one posted ahead of a request). The tests use it to hold such a read back until
  the server has aborted, standing in for a cold reader without relying on timing.
- `ExecuteScalar_ResetRefusedAndReadHeldBack_ReportsServerError` and the `ExecuteScalarAsync`
  variant: a pooled connection is reused with the reset pending and any read posted on demand held
  back until the abort; the `SqlException` must carry the server's 4021 (and 596), not a
  transport error. Without the early read both fail with "Expected: 4021, Actual: 10054"
  (verified by disabling the early post); with it both pass.
- `ExecuteScalar_PooledReuse_ReturnsResult` and the async variant: against a server that accepts
  the reset, a pooled reuse with the early read still returns results for two consecutive
  commands.

Coding policies: the change was checked against `policy/coding-style.md` and
`policy/coding-best-practices.md` (100-column lines, Allman braces, field naming and placement,
no non-ASCII literals, no BOM on new files; the one `Task.Wait()` is justified in a comment and is
the synchronous path's equivalent of the SNI's blocking read; the new path traces through
`SqlClientEventSource`; the tests do not rely on timing).

Existing suites on this branch, net10.0, Debug configuration:

- Unit: 2257 passed (including the 4 new tests), 0 failed, 9 skipped.
- Functional: 1386 passed, 16 failed, 15 skipped; the 16 are identical on `main` on this machine
  (14 `CultureNotFoundException` in culture-specific comparisons, 2 need administrator rights for
  the certificate store).
- Manual sets 1-3 were run against the prototype this is derived from (same logic plus tracing),
  with only environment-dependent failures (SQL authentication, elevated process, Danish locale).

Manual verification with a repro that creates a database, runs `ALTER DATABASE … COLLATE` on a
pooled connection, returns it to the pool, reopens and runs a command, each in a fresh process:

| Case | Before | After |
|---|---|---|
| LocalDB (named pipes), MARS, async | `-1` "Session Provider, error: 18" every run | 4021, 6/6 |
| LocalDB, no MARS, cold sync / cold async | 233 in 2 of 3 processes | 4021, 3/3 and 3/3 |
| SQL Server 2022, TCP, MARS / no MARS, async | 10054 or "Session Provider, error: 18" | 4021, 3/3 and 3/3 |
| Same, with a 5 ms delay injected before every read (prototype build) | lost in 6/6 | 4021 in 6/6 |
| Refused `INSERT` executed | 0/n | 0/n (unchanged) |

Suggested release-note entry (`release-notes/`, Fixed): "Fixed the server's error being reported
as a transport-level error when SQL Server refuses to reset a pooled connection and aborts the
session before the driver has posted its read."

🤖 Generated with [Claude Code](https://claude.com/claude-code)
