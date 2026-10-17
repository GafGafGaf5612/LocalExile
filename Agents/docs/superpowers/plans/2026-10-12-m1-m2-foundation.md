# M1 (Data Pipeline) + M2 (Protocol Recon) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the .NET solution, extract and schema-decode Path of Exile 3.28.0f game data from `Content.ggpk`, and produce the first protocol reverse-engineering artifacts for the login and gateway servers.

**Architecture:** A C#/.NET 10 solution. `GameDataExtractor` (console) consumes `Content.ggpk` through LibGGPK3/LibBundle3, reads bundle indexes and `.dat64` tables, and decodes rows against the community `dat-schema`. `GameServer.Data` loads the decoded output into typed collections for later simulation. Protocol recon uses REA/Ghidra on `Game.exe` plus local string extraction, writing findings into `Agents/docs/re/`.

**Tech Stack:** .NET 10, C#, LibGGPK3/LibBundle3 (NuGet), Game-tool-dev/dat-schema (JSON), xUnit, REA 6.4.0 + Ghidra 12.1.4.

**Spec:** `Agents/docs/superpowers/specs/2026-10-12-Game-3280f-private-server-design.md`

## Global Constraints

- Target client version: Path of Exile `3.28.0f`, patch `3.28.0.8`. No other version is supported.
- .NET 10 SDK only (`dotnet --version` = 10.0.401).
- `Client/`, `Tools/`, and `data/` are gitignored; never commit them.
- All source under `src/`, `tools/`, `tests/`; docs under `Agents/docs/`.
- Precipitation-free: extraction reads the GGPK read-only; never write to `Client/`.
- Reuse OSS (LibGGPK3, dat-schema); do not reimplement GGPK/Oodle.

## Review Focus

- Malformed/truncated `.dat64` (harness reads a bad offset): reader must throw a typed error, not return garbage rows.
- GGPK record whose length is 0 or exceeds the file: extractor must skip + record, not crash.
- `.dat64` string field with invalid UTF-8/UTF-16: decode lossy but keep row, flag it.
- dat-schema missing a table present in-data: extractor emits a "schema-missing" report, not a silent skip.
- Empty table (0 rows): reader returns an empty list, no exception.

---

### Task 1: Solution skeleton

**Files:**
- Create: `GameServer.sln`, `src/GameServer.Core/GameServer.Core.csproj`, `src/GameServer.Data/GameServer.Data.csproj`, `tools/GameDataExtractor/GameDataExtractor.csproj`, `tests/GameServer.Data.Tests/GameServer.Data.Tests.csproj`, `Directory.Build.props`

**Interfaces:**
- Produces: a buildable solution; `net10.0`; nullable enabled; implicit usings enabled.

- [ ] **Step 1:** `dotnet new sln -n GameServer` in repo root.
- [ ] **Step 2:** Create the four projects (`classlib`, `console`, `xunit`) and `dotnet sln add` each.
- [ ] **Step 3:** Add `Directory.Build.props` with `<TargetFramework>net10.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- [ ] **Step 4:** `dotnet build` → succeeds.
- [ ] **Step 5:** Commit.

### Task 2: dat64 reader (schema-driven)

**Files:**
- Create: `src/GameServer.Data/Dat/DatFile.cs`, `src/GameServer.Data/Dat/DatSchema.cs`, `src/GameServer.Data/Dat/DatValue.cs`
- Test: `tests/GameServer.Data.Tests/DatFileTests.cs`

**Interfaces:**
- Produces: `DatFile.Read(ReadOnlySpan<byte> bytes)`; `DatSchema.FromJson(string json)`; `DatFile.Rows(DatTableSchema, int) -> IReadOnlyList<Dictionary<string, DatValue>>`.

- [ ] **Step 1:** Failing test: a synthetic 2-row table with one `int` and one `string` column decodes to the expected values.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement `DatFile` header (u32 rowCount, rows, then the `0xBB*8` reference block) and typed decoders for `bool,int,uint,long,float,string,reference`.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Failing test: truncated bytes throw `DatFormatException`.
- [ ] **Step 6:** Implement bounds checks; run → PASS.
- [ ] **Step 7:** Commit.

### Task 3: GGPK bundle extraction

**Files:**
- Create: `tools/GameDataExtractor/Program.cs`, `tools/GameDataExtractor/GgpkSource.cs`
- Reference: LibGGPK3 + LibBundle3 NuGet packages.

**Interfaces:**
- Produces: `GgpkSource.Open(string ggpkPath)`; `GgpkSource.ListFiles() -> IEnumerable<string>`; `GgpkSource.ReadFile(string path) -> byte[]`.

- [ ] **Step 1:** `dotnet add tools/GameDataExtractor package LibGGPK3` (and LibBundle3 if separate); confirm resolution.
- [ ] **Step 2:** Implement `GgpkSource` over LibGGPK3's bundled-GGPK/index API.
- [ ] **Step 3:** CLI: `GameDataExtractor list <ggpk>` prints counts; `GameDataExtractor extract <ggpk> <outDir> --path data/` extracts `Data/`.
- [ ] **Step 4:** Run `list` against `Client/Content.ggpk` → prints file count.
- [ ] **Step 5:** Commit.

### Task 4: Extract + decode Data tables

**Files:**
- Modify: `tools/GameDataExtractor/Program.cs` (add `decode` command)
- Create: `tools/GameDataExtractor/SchemaStore.cs`
- Test: `tests/GameServer.Data.Tests/ExtractSmokeTests.cs`

**Interfaces:**
- Consumes: `DatFile`, `DatSchema`, `GgpkSource`.
- Produces: `--out data/tables/*.json` (one file per table) + `data/extract-report.json`.

- [ ] **Step 1:** Fetch `dat-schema` `schema.min.json` into `data/schema/` (vendored; commit the schema, not the game data).
- [ ] **Step 2:** Implement `decode`: for each `Data/**/*.dat64`, decode with schema, write JSON; record schema-missing tables.
- [ ] **Step 3:** Failing smoke test: decode the vendored two-row fixture.
- [ ] **Step 4:** Run against real GGPK; assert `BaseItemTypes` (or `Stats`) decodes with >0 rows.
- [ ] **Step 5:** Commit (source only; `data/` stays ignored except the schema).

### Task 5: Protocol recon — endpoints & strings

**Files:**
- Create: `Agents/docs/re/001-client-endpoints.md`, `tools/GameProtocolCapture/` (scaffold)

**Interfaces:**
- Produces: a documented list of host/port strings, patch-protocol symbols, and TLS/pinning hints from `Game.exe`.

- [ ] **Step 1:** Extract ASCII+UTF-16 strings from `Client/Game.exe` (REA `search_strings` after `open_binary`, or local fallback) filtered to `login.Game.com`, `patch.Gamecdn.com`, `:6112`, `gateway`.
- [ ] **Step 2:** Record each hit with file offset and surrounding context in `Agents/docs/re/001-client-endpoints.md`.
- [ ] **Step 3:** Note REA/Ghidra analysis profile + any timeouts/limitations.
- [ ] **Step 4:** Commit.

### Task 6: Protocol recon — login & gateway handshake hypotheses

**Files:**
- Create: `Agents/docs/re/002-protocol-hypotheses.md`

- [ ] **Step 1:** From logs + strings, write the login flow (TLS/HTTP2 endpoints) and gateway flow (`:6112` framing hypotheses) as explicit, falsifiable hypotheses.
- [ ] **Step 2:** List the exact capture experiments needed to confirm each.
- [ ] **Step 3:** Commit.

### Task 7: Capture tooling scaffold

**Files:**
- Create: `tools/GameProtocolCapture/GameProtocolCapture.csproj`, `Program.cs`, `Agents/docs/re/captures/README.md`

- [ ] **Step 1:** Scaffold a console tool that records a local TCP loopback stream to a timestamped binary + index file.
- [ ] **Step 2:** Smoke test against a loopback echo.
- [ ] **Step 3:** Commit.

---

## Self-Review

- **Spec coverage:** M1 → Tasks 1–4; M2 → Tasks 5–7. Client patching (M3) and instances (M4) are intentionally out of this plan and get their own plan after M2 findings.
- **Step scan:** each step yields one checkable artifact.
- **Type consistency:** `DatFile`, `DatSchema`, `DatValue`, `GgpkSource` names are used consistently across Tasks 2–4.
- **Review Focus tests:** malformed dat64 (Task 2 Steps 5–6), zero-length GGPK record (Task 3), schema-missing (Task 4 Step 2), empty table (Task 2 Step 1 covers non-empty; add empty-case assertion), invalid string bytes (Task 2 lossy decode).
