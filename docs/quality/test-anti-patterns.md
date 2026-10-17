# Test Audit (`dotnet-test:test-anti-patterns` skill)

> 🇪🇸 [Leer en español](test-anti-patterns.es.md)

Review of 210 test methods (198 unit, 12 integration) with the
`test-anti-patterns` skill from [dotnet/skills](https://github.com/dotnet/skills).

**Result:** 0 critical, 7 high, 2 medium, 7 low. All 7 high-severity findings are fixed.

## High (fixed)

| # | Test | Problem | Fix |
|---|---|---|---|
| H1 | `ResumableStreamCanBeReadAgainAfterDisconnect` | `Task.Delay(50)` waiting for the chat to have no active stream | **Production:** `ResumableUIMessageStream` releases the chat before completing the stream; the delay is removed |
| H2 | `AnswersRootsSamplingAndElicitationRequests` | `Task.Delay(100)` waiting for an MCP notification | Bounded polling (max. 5 s) |
| H3 | `ScheduledAutomationsFireOnCronOnceAndEvents` | Assumed 24 h between runs; fails across Chile's daylight-saving changes | Compares local date and time in `America/Santiago` |
| H4 | `PdfFallsBackForMissingGlyphsAndCustomFonts` | Hard-coded Linux font path | DejaVuSans (with its license) in `Fixtures/` |
| H5 | `LaunchesRealStdioProcess` | Silent `return` without python3 (passed without testing anything) | `Assert.SkipWhen` |
| H6 | `LoadsFromDirectoriesAndSavesSkills` | `ContinueWith` accepted any exception | `Assert.ThrowsAsync<ArgumentException>` |
| H7 | `ScriptsDoNotInheritSecretsAndTimeOut` | 500 ms timeout also applied to the script that must finish | Default runner for `env.py`; the short one only for `lento.py` |

L1 (python3 detection via `PATH`) and L2 (environment variable restored in `finally`) were also fixed,
as was `SummarizeUsesMapReduceForLongTexts`, which depended on the arrival order of parallel calls.

## Pending (medium and low)

- **M1:** `WorkflowEngine` reads `DateTimeOffset.UtcNow` directly; adding a `TimeProvider` to the options
  would allow testing cron across daylight-saving changes without editing internal state.
- **M2:** nine long tests that verify several behaviors; split them when they are next modified.
- **L3–L7:** 1 s polling in `WorkflowTests`, weak assertions (chunker overlap, warning text,
  `Assert.All` over possibly empty lists), the name of `OtherConversationsCannotEditArtifacts`,
  `HttpClient`/scopes without `using`, and `aisdk_test_*` tables left behind in PostgreSQL.

## Observed strengths

Provider tests that capture the real HTTP request and compare exact URLs, headers and JSON; expected
values from independent sources (AWS SigV4 test vector, CRC32 `0xCBF43926`); exceptions with a specific
type; small, deterministic fakes; unique temporary directories cleaned up in `finally`.
