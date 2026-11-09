# Contributing

OpenIPC Viewer is a side-project NVR-style viewer for OpenIPC cameras.
Contributions welcome — please follow the conventions below to keep
review cycles short.

## Branches and PRs

- Work on a feature branch off `main`. Branch names: `feat/short-slug` or
  `fix/short-slug`.
- Open the PR against `main`. Squash-merge is the default; the squash
  commit message should explain the *why*, not restate the diff.
- Keep PRs small — one self-contained step per PR (e.g. "in-process
  recording + foreground service on Android" is one PR; the touch-UX
  follow-up is another).

## Build expectations

- `dotnet build OpenIPC.Viewer.slnx` must finish with **0 warnings,
  0 errors**. `TreatWarningsAsErrors=true` is enforced.
- `dotnet test OpenIPC.Viewer.slnx --no-build` must pass; the MediaMTX
  integration test auto-skips when the container isn't running.
- The build also compiles the React web console (`npm ci` + `npm run build`
  from `src/OpenIPC.Viewer.Web.Client`). Install Node if you touch the web UI;
  without it the target is skipped and the server builds API-only.
- CI runs the Windows / Linux / macOS matrix plus the Android and iOS heads
  on every code push (docs-only pushes are skipped); don't ignore red status.

## Code style

- Code, identifiers, log messages, commit messages, docs: English.
  (`docs/web-server.ru.md` is a translation of an English original; UI strings
  live in `Localizer.cs` and `Web.Client/src/strings.ts`. The "ru" language
  slot now carries Simplified Chinese — the code is kept for compatibility.)
- One blank line between members; sealed `partial` classes when XAML
  code-behind or `CommunityToolkit.Mvvm` source-gen is involved.
- Comments only where the *why* is non-obvious; well-named identifiers
  carry the *what*.

## Architecture rules (load-bearing)

- `App` references `Core` only. Don't add references to
  Infrastructure / Video / Devices from `App`.
- The platform trio (`IFileSystem` / `ISecretsStore` /
  `IHwDecoderFactory`) is wired per-platform in each head's
  `Composition.cs`. Shared registrations belong in
  `OpenIPC.Viewer.Composition.SharedComposition`.
- Keep a change inside the scope it claims. Where the roadmap
  ([docs/ROADMAP.md](../docs/ROADMAP.md)) puts something in a later phase,
  leave it there rather than folding it in opportunistically.

## Reporting issues

Open one in the GitHub issue tracker — pick the **Bug report** or
**Feature request** form and fill in the fields. For crash reports,
attach the most recent log file from the `logs/` folder in your
platform's AppData root (paths are in the README's *User data* section).
