# SharpPad

A LINQPad-style C# scratchpad for Linux. Avalonia desktop app: write C# in the editor, hit F5, get LINQPad-style rich `Dump()` output.

## Install

One-liner (detects your distro and CPU, then installs the matching package):

```bash
curl -fsSL https://raw.githubusercontent.com/stevedowling/SharpPad/main/install.sh | bash
```

The installer picks a `.deb` on Debian/Ubuntu, an `.rpm` on Fedora/RHEL/openSUSE, and a portable `.tar.gz` on Arch and everything else. Overrides: `SHARPPAD_VERSION=1.2.0` for a specific release, `SHARPPAD_METHOD=tarball` to force a method. Remove with `install.sh --uninstall`.

Prebuilt packages for each release (x64 and arm64) are on the [Releases page](https://github.com/stevedowling/SharpPad/releases). The app bundle is self-contained, but **compiling scripts still needs the .NET 10 SDK** installed (`dotnet-sdk-10.0`); the installer warns if it's missing.

## Requirements

.NET 10 SDK on the machine (the SDK, not just the runtime — scripts are compiled with `dotnet build`).

## Run from source

```bash
dotnet run --project src/SharpPad.App
```

## Building release packages

`packaging/build-linux-packages.sh <rid> <version>` produces a `.deb`, `.rpm`, and `.tar.gz` for one runtime (`linux-x64` or `linux-arm64`) into `dist/`. Needs the .NET 10 SDK, plus `fpm` and `rpm`/`rpmbuild` for the deb/rpm targets. The `release` GitHub Actions workflow runs this for both architectures and publishes the artifacts to a GitHub Release when you push a `v*.*.*` tag.

## Usage

Scripts are top-level statements; classes/records can follow below. `Dump()` works on anything and returns its input for chaining.

```csharp
#r "nuget: Newtonsoft.Json, 13.0.3"    // NuGet reference (version optional -> latest)
#r "/path/to/Your.dll"                 // local DLL reference
#connection "mydb"                     // named DB connection (Manage… in toolbar)

Enumerable.Range(1, 20).Where(n => n % 3 == 0).Dump("multiples of 3");
Query("select * from people where age > @a", new { a = 30 }).Dump();
```

With a connection active (directive or toolbar dropdown): `Connection` (open `IDbConnection`), `Query(sql, param)`, `Query<T>(sql, param)`, `QuerySingle<T>`, `Execute` — all Dapper-backed. Providers: sqlite, postgres, sqlserver, mysql.

Keys: F5 run, Ctrl+S save. Saved scripts live in `~/.sharppad/scripts` and appear in the sidebar. Connections are stored in `~/.sharppad/connections.json` (plain text — treat accordingly).

## How it works

Each run generates a real net10.0 console project under `~/.sharppad/build/<hash>/` (user code goes into `Program.cs` with directive lines blanked, so compiler line numbers match the editor), builds it with `dotnet build` — MSBuild/NuGet do all reference resolution — and runs it as a child process. Output streams back as JSON frames on stdout (`Dump()` trees, redirected console, exceptions); Stop kills the process tree. First run of a script restores packages (a few seconds); subsequent runs are incremental (~1–2 s).

`SHARPPAD_HOME` overrides `~/.sharppad`; `SHARPPAD_DOTNET` overrides the `dotnet` binary used for script builds.

## Tests

```bash
dotnet test          # engine end-to-end (real builds/processes) + headless UI smoke tests
```

## Known limitations / v2 seams

- No IntelliSense — syntax highlighting only (TextMate). Seam: RoslynPad's editor packages, or a Roslyn completion service over the generated project.
- No typed LINQ-to-database (LINQPad's headline feature). SQL + Dapper + LINQ over results instead. Seam: EF Core `dotnet ef dbcontext scaffold` into the generated project — the project-per-script model was chosen partly to make this drop in cleanly.
- `Dump()` output is fully materialized up to limits (depth 5, 1000 items, then truncation markers) rather than lazily expandable across the process boundary.
- Scripts run to completion; no `Util.Cache`, no charting, no `Dump()` refresh.
- Console input (`Console.ReadLine`) is not wired up.
- Avalonia pinned to 11.3.x (12.x was released after this was written and hasn't been evaluated).
- Known security advisory (pre-existing, not introduced by the .NET 10 migration): `Microsoft.Data.Sqlite` transitively bundles `SQLitePCLRaw.lib.e_sqlite3`, which ships SQLite 3.49.1 — vulnerable to CVE-2025-6965 (memory corruption from a crafted aggregate query, fixed in SQLite 3.50.2). As of this writing even the latest `Microsoft.Data.Sqlite` (10.0.10) still bundles 3.49.1, so there is no clean version-bump fix yet; `dotnet build` will emit NU1903. Practical risk for a local scratchpad where you run your own SQL against your own databases is low. Revisit when a `SQLitePCLRaw` release bundling SQLite 3.50.2+ ships.
