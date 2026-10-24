# Magic Monkei HATS for macOS 2.3.0

Compact C# desktop app for Apple Silicon and Intel Macs. One universal DMG contains one app with both native runtimes; macOS selects the correct architecture automatically. Requires macOS 14 or later. Python and .NET are bundled dependencies only where needed during development; users do not need either installed.


Version 2.3.0 matches the compact Windows layout: a complete header logo,
Options and Info beside Install HATS, and an always-open log that expands with
the window and follows new entries after layout. Backups default to Desktop/HATSBackups.
The removed Options screens stay removed; automatic data protection still runs.

App updates are checked at startup and manually in Options. The Mac updater
verifies the universal DMG, stages a signed bundle, preserves quarantine and
keeps the previous app before restarting. See [PUBLISHING-UPDATES.txt](PUBLISHING-UPDATES.txt).
This source package does not publish a release or change an SD card.
## Build on GitHub

Open **Actions → Build universal Magic Monkei HATS DMG → Run workflow**. Pull requests and pushes to `main` that change the app or build files also start a build.

The workflow tests and compiles on separate Apple Silicon and Intel Mac runners. It combines native app hosts into a universal executable, creates and verifies a real compressed DMG, and launches that exact packaged app on both Mac types. After every job passes, download **Magic-Monkei-HATS-Universal-macOS** from the run's artifacts. Extract the artifact ZIP to get `Magic-Monkei-HATS-Universal.dmg`, its checksum and installation notes.

The build uses ad-hoc code signatures. It does not use Apple Developer ID signing or notarization. See [INSTALL.txt](INSTALL.txt) for the standard per-app first-open procedure. A public release should be Developer ID signed and notarized with the owner's Apple credentials.

## Protection and updates

- Releases and `hats-exclusions.json` come from **TechRepairs4U/HATS**, independently of this source repository.
- The latest stable release is discovered through GitHub; tags and ZIP names are not hard-coded. Drafts and prereleases are excluded, as in the current Windows app.
- The CFG is fetched from the repository's current default branch immediately before backup or SD changes. Invalid, missing, mismatched or unavailable data stops the operation. There is no cached fallback.
- GitHub exclusions, optional local exclusions and automatic game/port/mod protections are combined. Protected paths are not backed up, deleted or overwritten.
- Recognised HATS apps/overlays and known HATS sysmodules still update. Unrelated `switch` folders and game content in `atmosphere/contents` stay protected.
- Copies and backups are verified; failed writes attempt recovery from the backup. The exact CFG is recorded with each recovery plan.
- Only mounted physical, external/removable FAT32 or exFAT volumes are accepted. The card's identity and mounted filesystem are checked before writing; disconnects and changes stop the job.
- The Mac app opens Disk Utility for formatting preparation. It does not format drives itself.
- Logs, backups and downloaded working files are retained. There is no B2 connection or credential in this app.

The Python files at the repository root are the previous implementation and are retained. They are not used by this C# build. The previous README and workflow are archived under `legacy/`.

## Source layout

- `source/Hats.Core`: installer, archive checks, releases, protections and Mac card validation.
- `source/Hats.Mac`: Avalonia desktop UI and Mac settings.
- `source/Hats.Mac.Tests`: portable regression suite, Mac checks and a headless window render.
- `packaging`: universal app-host creation, signing, disk-image creation and packaged startup checks.

The existing Windows WinUI and Python builds are separate and unchanged.

## Local development

Install the .NET 10 SDK. Run `dotnet run --project source/Hats.Mac` on a Mac.

Run tests with `dotnet run --project source/Hats.Mac.Tests -c Release`. Add `-- --render` to render the compact UI without an interactive window. Set `HATS_TEST_ROOT` to an existing writable, non-symlink folder for retained test fixtures. For a real downloaded HATS pack, add `-- --real-pack /absolute/path/to/pack.zip --live-exclusions --verification-report /absolute/path/to/report.json`.

The checked-in `protection-test-fixture.json` is test data only. The application does not load it or use it as a fallback. Local Windows verification passed 45 installer/regression checks, additional Mac metadata checks and the 359-path exclusion plan against a real HATS pack. Mac-native startup and disk-image tests are performed by the workflow, and physical SD-card testing remains a separate hardware check.

## Build references

- [Avalonia macOS packaging](https://docs.avaloniaui.net/docs/deployment/macos/)
- [.NET macOS support](https://learn.microsoft.com/en-us/dotnet/core/install/macos)
- [GitHub Mac runner architectures](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
- [Apple statfs mount structure](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/mount.h)

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the frameworks used.

