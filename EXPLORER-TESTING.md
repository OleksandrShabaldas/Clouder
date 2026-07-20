# Testing the Explorer integration (Phase 3)

**Read this before switching the feature on.** The Windows Cloud Files API (CfApi) runs
inside Explorer's process space. A provider that misbehaves — blocks a callback, sends an
unaligned transfer, or never completes a request — can hang or crash Explorer. An earlier
build of Clouder did exactly that, which is why the integration was disabled.

The code has since been rewritten (see §"What changed"), but **it has not been verified
end to end on real hardware.** Everything below assumes it might still misbehave.

---

## Safety net first

Explorer crashing is annoying, not dangerous — but know the recovery steps *before* you
need them:

**If Explorer hangs or the desktop disappears:**
1. `Ctrl+Shift+Esc` → Task Manager (works even with no desktop).
2. Find **Windows Explorer** → right-click → **Restart**. If it isn't listed:
   **File → Run new task** → `explorer.exe` → OK.

**To turn the feature off when the UI is unreachable** — stop Clouder (Task Manager →
end `Clouder.App.exe`), then unregister the sync roots from PowerShell:

```powershell
# Lists every registered sync root; Clouder's are named "Clouder!pool-..."
[Windows.Storage.Provider.StorageProviderSyncRootManager, Windows.Storage.Provider, ContentType=WindowsRuntime]::GetCurrentSyncRoots() |
    ForEach-Object { $_.Id }

# Remove one by id
[Windows.Storage.Provider.StorageProviderSyncRootManager, Windows.Storage.Provider, ContentType=WindowsRuntime]::Unregister("Clouder!pool-XXXX")
```

Then start Clouder and leave the toggle off. The setting lives in the app database
(`%APPDATA%\Clouder\clouder.db`, `settings` table, key `clouder_config`), not the registry.

**Files are never at risk** from this feature: placeholders are metadata, and every byte
still exists in the cloud. Worst case is a folder that needs its placeholders recreated.

---

## Recommended: test in a VM first

A Windows 10/11 VM with a throwaway Google account is the right place for the first run.
If you'd rather test on your real machine, at least do it with a **new pool** pointing at
an empty folder and one small file — not your main pool.

---

## Test procedure

Do these in order and stop at the first failure.

### 1. Baseline (integration OFF)
- Accounts page shows your accounts as **Connected**.
- Drop a file in the pool folder → it appears in the cloud within a minute.
- Add a file in the cloud → it appears locally on the next sync.

If two-way sync isn't working yet, fix that first — Phase 3 sits on top of it.

### 2. Turn it on
- Settings → **Windows Explorer** → toggle on.
- Expected: the pool folder appears in Explorer's sidebar with the Clouder name.
- Watch `%APPDATA%\Clouder\logs\clouder-<date>.log` for
  `Explorer integration active for pool: <name>`.
- ❌ If Explorer hangs here, the problem is registration or `CfConnectSyncRoot`.

### 3. Placeholders appear
- Add a new file **in the cloud** (Drive web UI), then trigger a sync.
- Expected: it appears in the pool folder with the correct size, marked online-only
  (cloud icon), and **the local disk usage does not grow**.
- Check the log for `is available on demand`.

### 4. Hydration — the critical test
- Double-click the online-only file.
- Expected: brief pause, file opens with correct contents, icon becomes a green check.
- ❌ If it hangs forever → a callback isn't completing. Kill Clouder; Explorer should
  recover once the provider disconnects.
- ❌ If it fails with an I/O error → check the log for `Hydration failed`.

**Test a large file too (100 MB+).** The alignment bug that broke the old build only
shows up on transfers spanning several 4 MB blocks with short network reads.

### 5. Striped files
- Only if you have 2+ accounts in the pool. Force-stripe a file
  (Pools → Manage → Stripe Everything), then open it from Explorer.
- Expected: it opens correctly — chunks are fetched from several accounts and
  reassembled on the fly (`StripeRangeMapper` handles the range math).
- This path never worked before: striped files had no hydration route at all.

### 6. Free up space
- Right-click a hydrated file → **Free up space**.
- Expected: content is discarded, file stays visible, disk usage drops. Opening it
  re-downloads.

### 7. Turn it off again
- Settings → toggle off.
- Expected: sidebar entry disappears, no Explorer hang, the pool folder behaves as a
  normal folder, and the read-only attribute is cleared.
- Files that were online-only are **still listed in the cloud** but their local
  placeholders may vanish — that's expected; the data is safe remotely.

---

## What changed since the crashing build

| Problem | Fix |
|---|---|
| Callbacks blocked on `.Wait()`, stalling the filter's thread pool | Work runs on the thread pool; callbacks return immediately and complete via `CfExecute` |
| Transfers used whatever a single `Read` returned → unaligned mid-file blocks Windows rejects | `AlignedTransfer` fills whole 4 MB blocks; only the final block may be short (tested against 7-byte reads) |
| Placeholders stored the *remote* id, but hydration looked items up by *internal* id — so uploaded files could never hydrate | Placeholders carry the internal item id (`{poolId}\|{relativePath}`) |
| Striped files had no hydration path at all | `HydrationService` + `StripeRangeMapper` fetch and splice the right byte ranges from each chunk |
| Failed requests sometimes left the file handle hanging | Every path reports success or failure exactly once |
| `FETCH_PLACEHOLDERS` was answered with the wrong operation type | Answered with `TRANSFER_PLACEHOLDERS`; Clouder populates placeholders itself from its metadata |
| Read-only attribute set on the pool folder and never cleared | No longer set; cleared on unregister |
| No way to cancel an abandoned hydration | `CANCEL_FETCH_DATA` cancels the in-flight transfer |

## What is still unverified

Everything requiring real Windows: `CfConnectSyncRoot`, `CfCreatePlaceholders`,
`CfExecute`, `CfDehydratePlaceholder`, and how Explorer reacts to them. The logic
*around* those calls (range math, alignment, identity encoding, placeholder-vs-download
routing) is covered by 79 unit tests.

Known gaps, by design for now:
- Deleting or renaming a file inside a placeholder folder isn't yet handled through
  CfApi's `NOTIFY_DELETE` / `NOTIFY_RENAME` callbacks; the FileSystemWatcher path handles
  it instead, which is why the integration doesn't need those callbacks to be correct.
- `CacheSizeLimitMb` / `AutoDehydrateDays` settings still aren't enforced automatically —
  "Free up space" is manual for now.
