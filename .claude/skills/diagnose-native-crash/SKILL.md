---
name: diagnose-native-crash
description: Diagnose a hard crash where GimmeCapture vanishes with no dialog and nothing useful in the log — exit code 0xC0000005 / -1073741819, or a stack ending inside ONNX Runtime, SkiaSharp or FFmpeg interop. Use when the app "just closes itself", dies during OCR / AI scan / translate / recording, or someone pastes a crash stack that bottoms out in native code.
---

# Diagnosing a native crash (access violation)

## What you are looking at

Exit code `-1073741819` is `0xC0000005`: an **access violation**. Native code dereferenced memory it
does not own — almost always a handle that was already freed.

This is NOT a .NET exception. It cannot be caught:

- `Program.Main`'s `try/catch` never sees it.
- `AppDomain.UnhandledException` usually does not run.
- `AppLog` records nothing at the moment of death.

So "there is nothing in the log" is expected, and is not evidence of anything. The app simply
disappears. Do not conclude from an empty log that the crash is unrelated to whatever the log last
showed.

## 1. Get the real evidence before theorising

A pasted stack is almost always truncated at the interesting end — the top frames, which name the
faulting call. Get all three of these yourself. **Do not propose a fix until you have them.**

The managed stack:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'} -MaxEvents 3 |
  ForEach-Object { "=== $($_.TimeCreated) ==="; ($_.Message -split "`n" | Select-Object -First 25) -join "`n" }
```

The faulting module and — critically — the path of the binary that died:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'} -MaxEvents 4 |
  ForEach-Object { "$($_.TimeCreated) | $(($_.Message -split "`n" | Select-String 'Faulting application path|Faulting module name') -join '  ')" }
```

The app's own log, which brackets each run with `Application.Started` / `Application.Stopped`:

```powershell
Get-Content "$env:LOCALAPPDATA\GimmeCapture\logs\gimmecapture-$(Get-Date -Format yyyyMMdd).log" -Tail 40
```

`Started` with no matching `Stopped` marks a hard crash. Logs live under
`%LOCALAPPDATA%\GimmeCapture\logs\` — one shared directory (daily files, 7-day retention), not
per-install-instance.

Note `AppLog.Record` deliberately logs exception **type + HResult + stack, never
`Exception.Message`**. Do not expect message text from logged exceptions.

## 2. Confirm which binary actually crashed

"Faulting application path" tells you Debug vs Release. Compare its timestamp against the fix you
believe is in it:

```powershell
Get-ChildItem "src\GimmeCapture\bin\Debug\net10.0-windows10.0.19041.0\GimmeCapture.dll" |
  Select-Object FullName, LastWriteTime
git log -3 --format="%h %cd %s" --date=format:"%Y-%m-%d %H:%M:%S"
```

If the binary predates the commit, the report says nothing about your fix — ask for a rebuild
instead of theorising. **This has already cost a full round twice.** It happens because a running
GimmeCapture locks the Debug apphost (`dotnet build -c Debug` fails with MSB3027), so work gets done
in Release while the user keeps running a stale Debug build. Never kill the user's app to free the
lock; ask them to close it and rebuild.

## 3. Correlate with the log, and know which lines lie

Line up the crash timestamp against the log. Two things have already produced wrong conclusions:

- **Doubled `MemoryTrim.Activity.*` lines are one call logged twice** — `NotifyActivity` notifies two
  schedulers and each logs. They are NOT two concurrent consumers.
- **A silent teardown looks like something else.** `OcrRuntime.Loaded` / `OcrRuntime.Unloaded` exist
  now; before them the OCR sessions were torn down with no log line at all, so a rebuild looked like
  a language switch. If the log seems to contradict the code, check whether the interesting event is
  simply unlogged — then add the line.

## 4. The pattern behind every one of these so far

Four crashes, three different objects, one shape:

> **A native handle borrowed across an `await`, whose owner can free it in the meantime.**

| What was borrowed | Who freed it mid-use |
|---|---|
| `InferenceSession` | a language switch calling `ForceUnload` |
| `InferenceSession` | nobody — two threads called `Run` on it at once, which is itself fatal |
| `SKBitmap` (frozen frame) | the snip window disposing it on close, while the scan was still running |

Check first, in this order:

1. Is a native object read **after** an `await` — including inside a `Task.Run` body?
2. Can its owner be torn down during that window? Closing the snip window disposes the frozen frame;
   releasing the last OCR lease disposes the sessions.
3. Can two callers reach the same native object at once? Cancelling does **not** stop work already
   started — `InferenceSession.Run` takes no cancellation token, so a cancelled scan runs to
   completion while its replacement begins.

## 5. Reuse the primitives that already exist

Do not invent another locking scheme:

- `Services/Core/Infrastructure/ResourceUseGate.cs` — many concurrent users vs one exclusive
  teardown, writer-preferring, with a timeout so a wedged user degrades to "skip the teardown"
  rather than blocking forever. Unit-tested.
- `OcrRuntimeService.BeginSessionUse()` — borrow scope over the ONNX sessions **and** the dictionary,
  which also serialises inference. Callers never touch a raw `InferenceSession`.
- `Services/Core/Infrastructure/IdleReleaseScheduler.cs` — defer a teardown so rapid re-use cancels
  it instead of forcing a dispose/recreate cycle.
- Ownership transfer beats sharing: `AIScanSessionRequest.PreCapturedFrame` is a private copy the
  service disposes, because the scan outlives the overlay that started it.

`SAM2RuntimeService.GetSessions()` still hands out raw sessions and has not been given the same
treatment.

## Rules

- **Get the stack first.** Two fixes here were shipped off timing correlation alone and neither was
  the cause. Correlation over n=3 is a hypothesis, not a diagnosis.
- **State plainly when a fix is unverified.** These crashes cannot be covered by an automated test —
  that needs real native sessions racing real teardown. Test the *mechanism* (the gate, the ownership
  contract) and say clearly that the crash itself is verified only by hand.
- **Say so when you were wrong.** Each round here corrected the previous theory, and the corrections
  were what made the next round land.
- After changing anything in this area, run
  `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1`.
