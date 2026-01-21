# Porting map: netcoredbg -> SharpDbg xUnit tests

This document describes which MI-based netcoredbg tests we have migrated into `tests/SharpDbg.Cli.Tests` and keeps a short status note so we can track missing coverage. Each section links the SharpDbg test(s) to the original MI script in `netcoredbg/test-suite`.

## Ported MI tests

### MiMode_LaunchAndBreakpointTests
- SharpDbg test: `tests/SharpDbg.Cli.Tests/MiMode_LaunchAndBreakpointTests.cs`
- Purpose: replicate the simple MI sequence that inserts breakpoints, runs the debuggee with `--stop-at-entry`, observes `*stopped` events, deletes the breakpoint, and exits cleanly.
- Original source: `netcoredbg/test-suite/MIExampleTest/Program.cs`
- Key anchors (approximate line numbers):
  - `Label.Checkpoint("init", "bp_test", …)` — line 175
  - breakpoint site: `Console.WriteLine("A breakpoint \"bp\" is set on this line"); Label.Breakpoint("bp");` — line 184
  - `Label.Checkpoint("bp_test", "bp2_test", …)` — line 186
  - `Label.Checkpoint("finish", "", …)` — line 195
- Status: Completed for the single-breakpoint flow and async stop handling; the original also exercises the second breakpoint (`bp2_test`) which can be ported later as an extension.

### MiMode_MITestBreakpointTests
- SharpDbg test: `tests/SharpDbg.Cli.Tests/MiMode_MITestBreakpointTests.cs`
- Purpose: provide a minimal MI flow that touches `break-insert`/`exec-run`, confirms the debugger reports the stop, and cleans up via `break-delete` and `gdb-exit`.
- Original source: `netcoredbg/test-suite/MITestBreakpoint/Program.cs`
- Key anchors:
  - `Label.Checkpoint("init", "BREAK1_test", …)` — line 220
  - `Label.Checkpoint("BREAK1_test", "BREAK3_test", …)` — line 295
  - later checkpoints covering ID-aware breakpoints (`BREAK3`, `BREAK4`, `FUNCBREAK*`) — lines 373‑416
- Status: Partial. The xUnit version currently validates the core insert/run/delete scenario but does not yet recreate the ID-based/function breakpoints, condition checks, or the `just-my-code` toggle present in the original.

### MiMode_EvaluateTests
- SharpDbg test: `tests/SharpDbg.Cli.Tests/MiMode_EvaluateTests.cs`
- Purpose: stop the debuggee, obtain a frame id, and exercise `data-evaluate-expression` (using a simple literal) to ensure MI evaluation wiring is functional.
- Original source: `netcoredbg/test-suite/MITestEvaluate/Program.cs`
- Key anchors:
  - `Label.Checkpoint("init", "values_test", …)` — line 716
  - `Label.Checkpoint("values_test", "expression_test", …)` — line 753
  - extensive coverage of expression variants (nested tests, static members, lambdas, literals, conditionals, unary ops, function evaluation and coalescence) — lines 828‑1472
- Status: Partial. The SharpDbg test confirms the basic evaluate command but does not yet port the wide range of expression checks present in the original script.

### MiMode_EvaluateExpressionTests
- SharpDbg test: `tests/SharpDbg.Cli.Tests/MiMode_EvaluateExpressionTests.cs`
- Test app: `test/mi-integration/TestAppExpression/Program.cs` (builds the `a/b/tc/str` operands, bool flags, arrays, and static helpers that `MITestExpression` exercises so MI queries have concrete data).
- Purpose: mimic the MI sequence that sets breakpoints, uses `stack-list-frames` to read a frame id, and calls `data-evaluate-expression` for that frame, mirroring the workflow that exercises expressions tied to a stopped frame.
- Original source: `netcoredbg/test-suite/MITestExpression/Program.cs`
- Key anchors:
  - `Label.Checkpoint("init", "expression_test1", …)` — line 179
  - `Label.Checkpoint("expression_test1", "expression_test2", …)` — line 196
  - `Label.Checkpoint("expression_test2", "expression_test3", …)` — line 210
  - `Context.CalcAndCheckExpression(...)` blocks that evaluate `a + b`, `tc.a + b`, `str1 + str2`, `d + a`, and `a + 1` — lines 200‑251
  - `Label.Checkpoint("finish", "", …)` — line 225
- Status: Mostly ported. The SharpDbg test now hits the same expression breakpoints and validates `a + b`, `tc.a + b`, `str1 + str2`, `d + a`, `a + 1`, plus array indexing, nullable coalescing, bool logic, and static helper results (e.g., `Program.Greeting`, `Program.Multiply`). Extended expression families (lambdas, conditional/unary operators, struct child navigation, etc.) still need formal porting.

### MiMode_VariablesTests
- SharpDbg test: `tests/SharpDbg.Cli.Tests/MiMode_VariablesTests.cs`
- Purpose: stop the target inside `TestApp1`, read the top frame, and exercise `data-evaluate-expression` both for a literal (`1+2`) and for the program variable `i` so we exercise real variable resolution and validation.
- Original source: `netcoredbg/test-suite/MITestVariables/Program.cs`
- Key anchors:
  - `Label.Checkpoint("init", "setup_var", …)` — line 536
  - `Label.Checkpoint("setup_var", "test_var", …)` — line 621
  - sequences that test debugger attributes, notifications, eval flags, timeouts, and exceptions — lines 1111‑1265
  - final cleanup block `Label.Checkpoint("finish", "", …)` — line 1265
- Status: Partial. SharpDbg now asserts an actual local value (the `i` variable from `TestApp1`) in addition to a synthetic arithmetic expression, but the richer `-var-*` coverage (attributes, eval flags, notify-of-cross-thread, timeout/exception handling) still needs to be ported.

### MiMode_SteppingTests
- SharpDbg test: `tests/SharpDbg.Cli.Tests/MiMode_SteppingTests.cs`
- Purpose: start the debuggee, insert a line breakpoint on the test apps, run to the breakpoint, and issue MI stepping commands such as `exec-next` and `exec-step` to confirm `*stopped` notifications arrive.
- Original source: `netcoredbg/test-suite/MITestStepping/Program.cs`
- Key anchors:
  - `Label.Checkpoint("init", "step1", …)` — line 324
  - successive checkpoints that exercise `step1`, `step2`, `step_in`, `step_over`, and the nested method calls inside `test_func1`/`test_func2` — lines 335‑376
  - further checkpoints that cover attribute-driven stepping, property accessors, cast handling, breakpoint stepping, and step arguments/compilation helpers — lines 399‑718
- Status: Partial. The SharpDbg test focuses on the basic `exec-next`/`exec-step` behavior. Full conditioning, attribute-, JMC-, and property-aware stepping scenarios still need to be ported from the netcore script, plus the richer fixture that generates many labeled breakpoints.

## Gaps & next steps

- Expand `MiMode_EvaluateTests` and `MiMode_EvaluateExpressionTests` to cover the remaining expression variants from `MITestExpression` and the nested value/child checks from `MITestEvaluate` (lambdas, static members, method calls, conditional/unary operators, and struct evaluations) beyond the sums already asserted.
- Port the remaining `-var-*` command coverage from `MITestVariables`, including attribute checks, `evalFlags`, notify-of-cross-thread, and timeout/exception handling.
- Broaden stepping coverage by porting additional checkpoints from `MITestStepping` (JMC/step filtering toggles, property accessor breakpoints, argument/compilation checks, etc.).
- Track other MI suites that still need porting (e.g., `MITestAsyncStepping`, `MITestException`, `MITestEvalArraysIndexers`, `MITestHotReload*`, and the larger `MITest*` family listed under `netcoredbg/test-suite`). Use this doc to call out whichever suite you pick next so we can keep the table up to date.
