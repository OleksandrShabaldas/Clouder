# Clouder

**Pool several cloud storage accounts into one folder on Windows.**

Clouder combines the free space of multiple cloud accounts — Google Drive and MEGA today —
into a single local folder that syncs both ways. Drop a file in, and Clouder decides which
account has room for it and uploads it there. You get one 45 GB folder instead of three
15 GB accounts you have to think about individually.

It's a native Windows app: C# / .NET 8 with a WinUI 3 interface, a tray icon, and no web
view anywhere.

> **Status: personal project, used in earnest but not widely tested.** It works, the sync
> logic is covered by 107 tests, and it's what the author runs. But it has been exercised
> on essentially one machine. Read [Known limitations](#known-limitations) before you point
> it at data you can't afford to lose — and keep another backup of anything irreplaceable.

---

## Contents

- [How the pooling works](#how-the-pooling-works)
- [Features](#features)
- [Install](#install)
- [Updates](#updates)
- [First run](#first-run)
- [Connecting accounts](#connecting-accounts)
- [Sync behaviour](#sync-behaviour)
- [Windows Explorer integration](#windows-explorer-integration-experimental)
- [Settings reference](#settings-reference)
- [Where your data lives](#where-your-data-lives)
- [Building from source](#building-from-source)
- [Project layout](#project-layout)
- [Known limitations](#known-limitations)
- [License](#license)

---

## How the pooling works

A **pool** is a local folder plus a set of cloud accounts that back it. Files placed in
the folder are uploaded to one of those accounts.

The default model is **JBOD** — each whole file lives on exactly one account. That's the
safe choice: if one provider is down, only that provider's files are unavailable, and
everything else keeps working. Striping across accounts is available, but it's opt-in and
reserved for the one case that genuinely needs it (see [Striping](#striping-big-files)).

### Placement strategies

When a new file needs a home, the pool picks a member account using its strategy:

| Strategy | Behaviour |
|---|---|
| **Fill first** | Fill one account completely before touching the next. Keeps related files together and leaves the other accounts pristine. |
| **Round robin** | Spread files to balance *usage* across accounts, so they fill at roughly the same rate. |
| **Largest free** | Always place on whichever account currently has the most free space. |

### Fill tiers

Every member has a **tier** (lower numbers used first). Members sharing a tier form one
group that the strategy distributes across; Clouder only moves to the next tier when no
member of the current one can take the file.

So with accounts A, B, C at tier 0 and D at tier 1, files spread across A/B/C by the
strategy, and D is untouched until A, B and C are all full. Useful when one account is
slower, nearly full, or you'd rather keep it in reserve.

### Usage caps and reserved space

Cloud accounts usually hold other things besides your pool. Two per-member limits keep
Clouder in its lane:

- **Usage cap** — the pool will never store more than this much on the account, even if
  there's space left.
- **Reserved space** — always leave this much free on the account for everything else.

### File rules

Rules override placement for files matching a pattern, evaluated highest-priority-first:

| Match on | Then |
|---|---|
| File extension | Route to a specific account |
| Minimum file size | Route to a specific provider |
| Maximum file size | Use a different placement strategy |
| Folder path | Exclude from the pool entirely |
| Exclude pattern | |

Rules are **preferences, not hard constraints**. If a rule says "put videos on MEGA" and
MEGA is full, Clouder falls through to normal placement rather than refusing to store the
file. For a genuine hard constraint, use an `Exclude` rule.

### Striping big files

When a file is larger than the free space on *any* single account, Clouder offers three
choices rather than just failing:

1. **Cancel** the placement.
2. **Stripe** that one file into `.clpartNNN` chunks across several accounts. It's
   reassembled transparently on download. The trade-off: every account holding a chunk
   must be reachable to read the file back.
3. **Reorganize** — shuffle existing files between accounts to open up a contiguous gap
   big enough, so the file can be stored whole.

Reorganization can also run automatically when a pool fills up.

---

## Features

**Pools**
- Create, edit, merge and delete pools; each pool is a local folder plus member accounts
- Per-member tier, enable/disable toggle, usage cap and reserved space
- Manual **Sync Now** with live progress
- Bulk stripe-all / unstripe-all, and auto-reorganize with a plan preview before it runs
- Each pool gets a dedicated remote folder (`Clouder/{PoolName}`) on every member account,
  so it never touches the rest of your Drive

**Sync**
- Two-way: local changes upload, remote changes download
- `FileSystemWatcher` with a 2 s debounce, plus a periodic full sweep as a safety net
- Folder structure mirrored remotely; deletions propagate
- Conflict policies: newest wins, keep both, or ask — applied consistently in **both**
  directions
- Upload and download speed limits, shared as one budget across concurrent transfers
- Transfer history, minimum-free-disk guard, pause

**Storage management**
- Local cache with a size ceiling; least-recently-used eviction
- Automatic dehydration of files untouched for N days — the local copy is dropped, the
  cloud copy stays. Never applied to a file with unsaved work (see
  [Sync behaviour](#sync-behaviour))
- Quota tracking per account with 80% / 95% alerts

**Interface**
- **Dashboard** — total pooled storage, per-account and per-pool usage, colour-coded
- **Accounts** — guided Google Cloud Console walkthrough, MEGA login, connection badges,
  reconnect, quota refresh
- **Files** — browse everything in a pool, search, see which account holds each file,
  striped and versioned badges, download, chunk map, version history
- **Notifications** — severity filters, unread badge, and optional email monitoring that
  watches for storage-full / security / account-closure mail from Google and MEGA
- **Settings** — everything below, saved live and pushed into the running sync service
- Tray icon with open / sync now / pause / exit; minimize-to-tray on close

---

## Install

1. Download `Clouder-Setup.exe` from the [Releases page](../../releases/latest).
2. Run it. It installs to `%LOCALAPPDATA%\Clouder`, adds Start menu and desktop shortcuts,
   and launches Clouder when it's done.

No prerequisites: the build is self-contained, bundling both the .NET 8 runtime and the
Windows App SDK. That's why the download is large — but see [Updates](#updates), which
is why you only pay that cost once.

**Requirements:** Windows 10 version 1809 (build 17763) or newer, 64-bit.

SmartScreen will likely warn you, because the installer isn't code-signed (certificates
cost money and this is a hobby project). *More info → Run anyway*, or build it yourself
from source.

There's also a `Clouder-win-x64-Portable.zip` if you'd rather not install — unzip it and
run `Clouder.App.exe`. The portable build updates itself in place too.

## Updates

Clouder checks this repository's releases for a newer version every 24 hours, downloads
what it finds, and then **asks** whether to restart — a sync or a large upload in progress
is never interrupted without your say-so. If the window is minimized to the tray, you get
a notification instead of a dialog, and the update waits.

Updates are delta packages, so a typical one transfers a few MB rather than the full
~140 MB. Most of that download is the .NET runtime and Windows App SDK, which rarely
change between releases.

- **Check manually:** About → *Check for updates*.
- **Change the schedule or turn it off:** Settings → Updates.

Updates only work when Clouder is running from an installed or portable Velopack build. If
you run it straight out of a `dotnet publish` output folder, the update UI says so and
does nothing rather than failing confusingly.

---

## First run

1. **Connect at least one account** — Accounts → Add. See below; Google Drive needs a
   one-time setup, MEGA doesn't.
2. **Create a pool** — Pools → Create. Give it a name, pick a local folder, choose a
   placement strategy, and tick the accounts that back it.
3. **Put files in the folder.** They upload automatically. Use **Sync Now** to force a
   full sweep.

---

## Connecting accounts

### MEGA

Email and password. Clouder derives an `AuthInfos` session key from your password rather
than keeping the password in the clear as its primary credential, and stores that session
so you don't have to log in again. Your password is *also* kept, encrypted with Windows
DPAPI (readable only by your Windows user, on this machine), so an expired session can be
renewed silently instead of blocking sync until you next open the app.

> ⚠️ **MEGA deletions are permanent.** Clouder deletes with `moveToTrash: false`, so a
> file deleted from a MEGA-backed pool does not go to MEGA's Rubbish Bin. There is no undo.

### Google Drive

Google Drive requires **your own** OAuth Client ID and Secret. Clouder can't ship a shared
one: Google's verification process doesn't cover an app that a random person installs to
access their own Drive, and an unverified shared client would hit a hard 100-user cap and
show a scary warning screen to everyone.

The Accounts page walks you through it in six steps with clickable links and paste buttons,
including what to do about the common 403 errors. The short version:

1. Create a project in the [Google Cloud Console](https://console.cloud.google.com/).
2. Enable the **Google Drive API**.
3. Configure the OAuth consent screen as **External**, and add your own Google account as
   a **test user** — this is the step people miss, and skipping it causes `403
   access_denied`.
4. Create an **OAuth client ID** of type **Desktop app**.
5. Copy the Client ID and Client Secret into Clouder.
6. Sign in through the browser window that opens.

The credentials are stored locally and reused for every Google account you add afterwards.
Tokens live in a separate directory per account, so multiple Google accounts work
simultaneously.

---

## Sync behaviour

**What gets synced.** Only the pool's own remote folder (`Clouder/{PoolName}`) on each
account. Everything else in your Drive is invisible to Clouder and is never touched.

**First poll doesn't import.** When a pool member is first tracked, Clouder records a
change cursor but does *not* download files that already exist remotely. You get changes
from that moment forward, not a surprise multi-gigabyte download.

**Change detection.** Google Drive has a real delta feed, so Clouder uses it. MEGA has no
change feed, so its provider returns a full listing and the sync layer infers deletions by
comparing against what it tracks.

**No sync loops.** A downloaded file is written to a temp file, moved into place, then
stamped with the remote's modification time and tracked with that same timestamp. The
uploader skips any file whose local mtime isn't newer than what's tracked, so a download
can't trigger an upload of itself.

**Uploads replace safely.** When an edited file is re-uploaded, the new copy is fully
uploaded *before* the old cloud copy is deleted. A failed upload can never destroy your
only cloud copy — which matters especially on MEGA, where deletes are permanent.

**Dehydration is conservative.** Automatic eviction only touches files that are tracked,
fully synced, present locally, not already online-only, and not modified since the last
sync. Every one of those conditions guards a case where "the cloud has it" would be a
false assumption.

---

## Windows Explorer integration (experimental)

Clouder can register pools as Windows Cloud Storage Providers via the Cloud Files API
(CfApi) — appearing in Explorer's sidebar next to OneDrive, with on-demand placeholder
files and sync-status overlays.

> ⚠️ **This is off by default and you should treat it as unverified.** CfApi callbacks run
> in Explorer's process space, and a provider that misbehaves can hang or crash Explorer.
> An earlier build of Clouder did exactly that. The code has since been rewritten to fix
> every known cause, but the P/Invoke paths have not been confirmed working on hardware
> other than the author's.

**Your files are not at risk** — placeholders are metadata, every byte still exists in the
cloud, and the worst case is a folder whose placeholders need recreating. The risk is to
Explorer's stability, not your data.

If you want to try it, read **[EXPLORER-TESTING.md](EXPLORER-TESTING.md)** first. It has
the recovery steps for a hung Explorer and the PowerShell commands to unregister sync roots
when the UI is unreachable. Enable it at Settings → Windows Explorer.

---

## Settings reference

| Setting | Default | What it does |
|---|---|---|
| Auto-start on login | off | Registers Clouder in the `Run` key |
| Minimize to tray | on | Closing the window hides it instead of exiting |
| Show notifications | on | Windows toasts. Events are recorded either way — this only controls interruption |
| Default strategy | Fill first | Placement strategy for new pools |
| Sync interval | 300 s | How often the full sweep runs |
| Conflict policy | Newest wins | Newest wins / keep both / ask |
| Max concurrent transfers | 4 | Parallel uploads and downloads |
| Upload / download limit | unlimited | Shared budget across all transfers |
| Cache size limit | unlimited | Ceiling before least-recently-used eviction |
| Auto-dehydrate after | off | Drop local copies of files untouched this many days |
| Min free disk | 1024 MB | Downloads stop rather than filling the drive |
| Striping prompt threshold | 100 MB | Files above this may be offered striping |
| Auto-reorganize when full | on | Shuffle files to make room instead of failing |
| Check for updates automatically | on | Poll GitHub releases; you still choose when to restart |
| Update check interval | 24 h | How often to poll |
| Explorer integration | off | See the section above |

---

## Where your data lives

```
%APPDATA%\Clouder\
├── clouder.db                  SQLite metadata: items, accounts, pools, rules,
│                               versions, stripe plans, notifications, settings
├── logs\clouder-YYYY-MM-DD.log Rotated application logs
└── tokens\
    ├── google-drive\{id}\      One OAuth token store per Google account
    └── mega\{id}\session.json  Serialized MEGA session
```

Your actual files live in the pool folder you chose, and in the cloud. Uninstalling is
deleting the app folder; removing `%APPDATA%\Clouder` additionally forgets your accounts
and pools.

---

## Building from source

**You need the .NET 8 SDK.** The `global.json` pins 8.0.100 with `latestFeature`
roll-forward. A .NET 7 SDK fails with `NETSDK1045`.

```powershell
git clone https://github.com/OleksandrShabaldas/Clouder.git
cd Clouder

dotnet test tests/Clouder.Storage.Tests/Clouder.Storage.Tests.csproj

dotnet publish src/Clouder.App/Clouder.App.csproj `
    -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish
```

The result lands in `publish\`; run `Clouder.App.exe` from there.

Note that **trimming is deliberately disabled**. A trimmed publish crashes on launch: the
linker sets `JsonSerializer.IsReflectionEnabledByDefault=false`, which breaks the config
service and the DPAPI credential store, and it strips XAML-activated types that WinUI
resolves by reflection. Re-enabling it would require JSON source generators and a trim-safe
XAML audit.

---

## Project layout

Single process — the app hosts the UI, tray icon, sync service, health checks and email
monitoring together. (An earlier two-process design with a separate background service was
abandoned; one process that minimizes to tray achieves the same thing with far less
machinery.)

| Project | Target | Contains |
|---|---|---|
| `Clouder.Core` | `net8.0` | Models, `ICloudProvider`, capability flags, provider registry, logging, validation |
| `Clouder.Storage` | `net8.0` | SQLite metadata store, pool manager and placement pipeline, rule evaluator, sync service, hydration, cache eviction, bandwidth limiter, health checks |
| `Clouder.Providers.GoogleDrive` | `net8.0` | OAuth, CRUD, paged listing, quota, revisions, range download, MD5 |
| `Clouder.Providers.Mega` | `net8.0` | Session auth, CRUD, quota, emulated range download |
| `Clouder.CloudFilter` | `net8.0-windows10.0.19041` | CfApi P/Invoke: sync root registration, placeholders, hydration callbacks |
| `Clouder.Email` | `net8.0` | IMAP (MailKit) and Gmail API monitors, alert pattern library, DPAPI protection |
| `Clouder.App` | `net8.0-windows10.0.26100` | WinUI 3 UI, tray icon, service hosting |

Hydration logic (`HydrationService`, `StripeRangeMapper`, `AlignedTransfer`) deliberately
lives in `Clouder.Storage` rather than `Clouder.CloudFilter`: the interesting parts are pure
computation — which chunks cover a byte range, how to keep transfers 4096-aligned when the
network returns short reads — and putting them in a plain `net8.0` assembly means they're
unit-testable. Only the unavoidable P/Invoke is left unverified.

**Tests:** 107 tests in `Clouder.Storage.Tests` covering placement strategies, fill tiers,
usage caps, file rules, striping and reorganization plans, conflict handling, cache
eviction, bandwidth limiting, hydration ranges and store round-trips. Providers, CfApi,
email and UI are not covered.

---

## Known limitations

- **Two providers.** Google Drive and MEGA. No Dropbox, OneDrive, pCloud, WebDAV or S3.
- **Google Drive needs your own OAuth credentials** — a one-time setup, guided in-app but
  still about five minutes of clicking in the Google Cloud Console.
- **Explorer integration is experimental and off by default.** See above.
- **MEGA deletes are permanent** and MEGA has no version history, so the Files page shows
  no revisions for MEGA-backed files.
- **Striped files need every chunk-holding account online** to be read back.
- **Not code-signed**, so SmartScreen warns on first run.
- **Windows only**, x64 (ARM64 and x86 build but aren't tested).
- **No test coverage** for the provider implementations, CfApi glue, email monitors or UI.
- **One machine per pool.** Clouder isn't designed for two computers syncing the same pool
  concurrently.

---

## License

Licensed under the [Apache License 2.0](LICENSE).
