# Porting map: netcoredbg -> SharpDbg xUnit tests

This file maps the new xUnit MI tests in `tests/SharpDbg.Cli.Tests` to their original test sources in the netcoredbg test-suite. The goal is to make audits and further porting simple and avoid missing test cases.

## MiMode_LaunchAndBreakpointTests

- New test: `tests/SharpDbg.Cli.Tests/MiMode_LaunchAndBreakpointTests.cs`
- Purpose: exercise `break-insert`, `exec-run` (with `--stop-at-entry`), and `break-delete`, and verify out-of-band `*stopped` notifications.
- Original source: `netcoredbg/test-suite/MIExampleTest/Program.cs`
- Key anchors (approximate line numbers in original file):
  - `Label.Checkpoint("init", "bp_test", ...)` — line 175
  - Breakpoint location: Console.WriteLine("A breakpoint \"bp\" is set on this line"); Label.Breakpoint("bp"); — line 184
  - `Label.Checkpoint("bp_test", "bp2_test", ...)` — line 186
  - `Label.Checkpoint("finish", "", ...)` — line 195

Notes: the xUnit test implements the minimal MI command sequence derived from this original script and listens for `*stopped` async MI records.

## MiMode_EvaluateExpressionTests

- New test: `tests/SharpDbg.Cli.Tests/MiMode_EvaluateExpressionTests.cs`
- Purpose: set breakpoints, run to stop, obtain frame id using `stack-list-frames` and perform `data-evaluate-expression --frame=<id>` to exercise the `ManagedDebugger.Evaluate` path.
- Original sources with relevant anchors:
  - `netcoredbg/test-suite/MITestExpression/Program.cs`
    - `Label.Checkpoint("init", "expression_test1", ...)` — line 179
    - Breakpoint `BREAK1` placement near: `int c = tc.b + b; Label.Breakpoint("BREAK1");` — line 194
    - Expression checks (`Context.CalcAndCheckExpression`) — lines 200-202, 214, 251 (these represent the variety of expressions tested: `a + b`, `tc.a + b`, `str1 + str2`, `d + a`, and a struct field evaluation `a + 1`).

Notes: the xUnit test covers the infrastructure needed to evaluate expressions against a stopped frame. Future porting should include specific expression checks from `MITestExpression` (e.g., `a + b`, `tc.a + b`, `str1 + str2`, `d + a`, `a + 1`) as separate xUnit assertions that mirror `CalcAndCheckExpression` expectations.

---

If you want, I can:
- Add direct links and exact line ranges (file:line) for each assertion and MI command in the netcoredbg tests.
- Add a checklist and schedule for porting all `MI*` tests into xUnit with pass/fail statuses.
