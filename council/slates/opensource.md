# Council slate — OPEN-SOURCE STRATEGY seat

Read-only review of `C:\Users\james\Workbench\tools\shadowcast\` (2026-08-06 build).
Scope: whether/how to open-source, license choice, repo structure, community
dynamics, maintenance burden, reputational value, dual-licensing. Product/market,
monetization economics and legal obligations belong to other seats.

---

## 0. Live-read corrections to the brief (verified this session, not recalled)

These change the strategy inputs, so they lead.

1. **The shipped exe contains no GPL-derived and no AMD-derived code.**
   - `native/Program.cs` invokes **ffplay only** (`FindTool("ffplay.exe")`, line 722),
     as a separate process, discovered on the user's machine — nothing bundled.
     If it is absent the app logs `ffplay.exe not found - video only` and keeps
     running. ffmpeg proper is not referenced anywhere in `native/`.
   - Recording is **`MediaRecorder` VP9/Opus → .webm in-page** (`mf-viewer.html`
     576-585), *not* NVENC. NVENC/`-movflags` belong to the retired mpv path.
   - Sharpening in the shipped viewer is an **SVG `feConvolveMatrix`** (`mf-viewer.html`
     135-136) whose filter id happens to be `cas`. The **AMD FidelityFX CAS GLSL is
     only in `mpv/shaders/cas.glsl`** — the retired path.
   - **Consequence:** excluding `mpv/` and `_retired-mpv/` from any repo removes
     every third-party-code entanglement. The license choice for the viewer is
     therefore **free** — nothing in the dependency graph forces copyleft. That is
     a strategy fact, and it is the opposite of what the brief implies.
2. **The source is already clean of secrets and personal paths.** Grep for
   `james|Workbench|C:\Users|password|token` across `*.cs *.py *.html *.ps1 *.csproj`
   returns nothing but false positives (`_emit`, `JOBOBJECT_...`). Only two
   sanitization jobs exist, and neither is in the code: the **memory file** (names
   James, his monitor, his audio endpoints, `Workbench\` paths, `[[workbench-direct-
   cluster-ssh]]`) and the **"ShadowCast" name** itself.
3. **The Deck bridge is not ShadowCast-specific.** `deck/deckpad.py` is 234 lines,
   stdlib-only, and does exactly one generic thing: expose a virtual Xbox-360 pad
   over HTTP from any Linux box. The only coupling is the string
   `"ShadowCast Virtual Pad"`. Same for `mcp/shadowcast_mcp.py`: it speaks to *any*
   HTTP endpoint serving `/status` + `/frame?w=&q=` (`SHADOWCAST_API`), so it is a
   generic "let a model see a screen and press buttons" server that happens to
   ship pointed at this viewer.

---

## 1. Lens review

### 1.1 The demand-side asset, and its clock

Genki deprecating Genki Arcade created a population of owners with a working
dongle and no first-party app. That is the entire strategic opening: an unserved,
self-identifying, searchable audience ("Genki Arcade discontinued", "ShadowCast 2
replacement"). It is also **decaying**. Orphaned-hardware demand does not wait —
owners migrate to OBS or a generic UVC viewer, or shelve the dongle. Whatever
value exists here is largest now and monotonically smaller each month. Any plan
whose first public artifact is more than a few evenings away is effectively a
plan to miss it.

Counterweight: the dongle is a **generic UVC device**, and the viewer is generic
too apart from a name-match. So the audience is really "cheap HDMI capture on
Windows with low latency", which is much larger — and much more crowded (OBS,
Camo, Elgato's own software, a dozen mpv/ffmpeg recipes). Being *the* Genki
replacement is a narrow, defensible niche with a short clock; being *a* capture
viewer is a wide, undefensible one. Pick the niche for positioning, but do not
build the artifact so it only works on one vendor's dongle.

### 1.2 What open-sourcing actually buys here

- **Hardware-variety bug reports** — genuinely valuable and unobtainable any
  other way. This project's substance is a pile of *device-specific empirical
  claims* (44.1k-only audio pin, MJPEG-beats-raw at 1080p60, 24MB rtbufsize,
  DWM attributes, WinGet alias trap). Every one is N=1 on one box, one monitor,
  one GPU, one Windows build. Outside users are the only falsification instrument
  available. This is the strongest pro-open argument and it deserves to be named
  as such.
- **Reputation / hiring signal** — real, but it lives in the **method, not the
  artifact**. "I wrote a capture viewer" is unremarkable. "I proved DirectShow
  adds latency invisible to timestamps by cross-correlating two wallclock-stamped
  ffmpeg streams, then certified four negatives so nobody re-walks them" is a
  strong, rare signal. The signal is transportable *without* a repo.
- **Adoption / funnel** — only matters if something downstream monetizes. Not my
  seat, except for one coupling: license and contribution policy decide whether
  that door stays open (§1.5).
- **Contributions** — realistically near zero. A Windows-only WinForms + WebView2
  + Win32-interop app with a hardware prerequisite has an extremely small
  contributor pool. Do not price this in.

### 1.3 What it costs — concretely, for a solo maintainer

- **Unsigned 109MB single-file .NET exe = a permanent trust tax.** Publishing
  binaries to GitHub Releases will trip SmartScreen and Defender heuristics for a
  large fraction of downloaders. That produces a steady, unavoidable stream of
  "is this a virus / Windows blocked it" issues, none of which you can fix without
  a code-signing certificate (recurring annual cost, plus reputation-building
  time for a new cert). **Shipping binaries is the single largest burden decision
  in this whole review, and it is separable from shipping source.**
- **Environment-diversity issues.** Every red flag is present: GPU preference
  registry pinning per-exe path, WebView2 runtime presence, Win10-vs-Win11 DWM
  attributes that silently no-op, audio endpoint sample rates, `ffplay` discovery
  via WinGet, non-NVIDIA GPUs, other UVC dongles with different pins. Realistic
  order of magnitude for a project that reaches even a few hundred users: **1-3
  issues/week, most unreproducible, 20-60 min each.** That is 1-3 h/week
  indefinitely against a stated capacity of ~zero.
- **You inherit Genki's abandoned support desk.** This is the underrated cost and
  it is caused by the *name*, not the license. A repo called "ShadowCast" will
  receive "my Genki dongle doesn't work", "does this support ShadowCast 4K",
  "Genki won't answer me" — support for someone else's hardware, from people who
  reasonably believe they have found the successor. Renaming is the cheapest
  single burden-reduction available and it is free today.
- **An abandoned public repo is a NEGATIVE reputational asset.** A repo with 40
  open unanswered issues and a last commit 18 months ago reads worse to a hiring
  manager than no repo at all. The mitigations are cheap (§1.6) but they must be
  decided *before* publishing, not after.

### 1.4 Component-by-component treatment (they should differ)

| Component | Files | Generic? | Burden if public | Verdict |
|---|---|---|---|---|
| **Documented know-how** | memory file → a writeup | fully | **zero** | **Publish first.** Scarcest asset, unforkable, no support surface, carries the whole career signal. |
| **Deck input bridge** | `deck/deckpad.py`, `.service`, `verify_pad.py` | fully (rename one string) | very low — 234 LOC, stdlib-only, stable kernel ABI | **Publish, Apache-2.0, standalone repo.** |
| **MCP layer** | `mcp/shadowcast_mcp.py` + the `/status`+`/frame` contract | mostly — already endpoint-agnostic | low — dependency-free, tiny surface | **Publish, Apache-2.0**, documented as a *contract* so it works against OBS or any frame source. |
| **Native viewer** | `native/*.cs`, `mf-viewer.html`, csproj | Windows-only, hardware-coupled | **high** — all of §1.3 | **Hold, or publish source-only under a burden-capping policy.** |
| **Retired mpv bundle** | `mpv/`, `_retired-mpv/` | n/a | n/a | **Exclude from every repo** (also removes the AMD CAS provenance question entirely). |

The single most important insight of this seat: **the artifacts with the highest
reputational return have the lowest maintenance cost, and they are separable from
the one artifact that carries almost all the burden.** There is no reason to
couple them.

### 1.5 License reasoning (strategy, not obligations)

- **AGPL-3.0 — reject.** Its distinguishing clause covers *network interaction*
  with users. A local desktop capture viewer has none, so AGPL buys essentially
  nothing over GPL here while carrying maximal deterrence: many companies
  blanket-ban AGPL, and it would kill the small-but-real chance of the MCP/input
  pieces being vendored somewhere useful. Cargo-culting AGPL "because it's the
  strongest" is a common and costly mistake.
- **Apache-2.0 over MIT for the small pieces.** Same permissiveness, plus an
  explicit patent grant and explicit contribution terms (§5 makes inbound
  contributions Apache-licensed by default). For code you *want* vendored into
  other people's tooling, Apache-2.0 is the lower-friction choice at corporate
  legal review. MIT is fine but strictly weaker on both counts.
- **GPL-3.0 for the viewer, if published.** Not for ideology: it stops a
  commercial reskin-and-sell fork of the one component that took the most work,
  while leaving *your* commercial path open because you hold all the copyright.
- **Dual-licensing is available only while you own 100% of the copyright.**
  This is the coupling the owner most needs to hear: **the first accepted code
  contribution without a CLA or copyright assignment permanently forecloses
  relicensing.** For a solo owner explicitly hunting income levers, that option
  is worth more than a trickle of drive-by PRs. Either require a CLA (cheap:
  cla-assistant bot) or state "bug reports and suggestions welcome; code PRs are
  not accepted" — both preserve the option; doing neither silently spends it.

### 1.6 Burden-capping devices worth adopting regardless of which path is chosen

Each is minutes of work, and each is far more effective than intending to
triage harder:

1. **Rename off "ShadowCast"** to something describing the capability, not one
   vendor's product. Broadens the audience, detaches you from Genki's brand and
   their orphaned customers' expectations, and sidesteps the trademark question
   the legal seat is handling.
2. **Do not publish binaries.** Source + build instructions. This is the highest
   ratio of burden removed to adoption lost of anything on this list.
3. **Issues disabled, Discussions enabled.** Users answer each other; an
   unanswered discussion thread does not read as a maintainer failure the way an
   unanswered issue does.
4. **A Support section that states the truth up front:** "Published as a
   reference implementation for a dongle whose vendor app was discontinued. No
   support. Reports are read, not triaged. This may never be updated." Explicit
   non-promises are respected; implicit ones are not.
5. **Archive rather than abandon.** An archived repo reads as *finished*; a stale
   one reads as *dropped*. Set a reminder to archive if untouched for 6 months.
6. **Rewrite, do not paste, the memory file.** It carries the owner's name, his
   monitor and audio hardware, `Workbench\` paths and a wikilink to the cluster
   SSH note.

---

## 2. TOP 5 STRATEGY PROPOSALS

### P1 — Knowledge-first: publish the writeup, hold all app code
**Recommendation.** One long technical post under his own name, neutrally titled
around the finding rather than the product (e.g. "Why your USB capture dongle
feels laggy on Windows: DirectShow vs MediaFoundation, measured"). Content: the
DirectShow-vs-MediaFoundation latency result and *how it was measured*
(two wallclock-stamped ffmpeg streams, cross-correlated), rtbufsize as a latency
knob with the 256MB = 1.03s number, raw-vs-MJPEG jitter table, the 44.1k-pin
resample pop chain, and the certified negatives. Publish
`measure-latency.ps1` alone as a gist under **MIT** so the method is reproducible.
Everything in `native/`, `mcp/`, `deck/` stays private. License the prose CC-BY or
just "all rights reserved, link freely" — prose licensing is not load-bearing here.
**Rationale.** The measurements are the scarce, unforkable, non-decaying asset;
the code is replaceable. This captures ~80% of the reputational value at ~0%
of the support burden, forecloses nothing, and is the only artifact whose value
does not depend on the owner having capacity later.
**Expected effect.** Search-durable presence on the exact queries orphaned owners
type; a real hiring/credibility artifact; a live read on whether demand exists.
**IMPACT 4 · CONFIDENCE 5 · EFFORT S**
**Falsification test (<1 day).** Before writing the long version: post the
DirectShow/MediaFoundation finding as a substantive comment on an existing
"Genki Arcade is discontinued, now what?" thread (Reddit/forum), and separately
as a short note to HN/Lobsters. Measure replies, upvotes and DMs in 24h. Fewer
than ~10 engaged responses across both ⇒ the demand thesis is weak and every
proposal below inherits that discount.

### P2 — Spin out the two generic, near-zero-burden components under Apache-2.0
**Recommendation.** Two small standalone repos, viewer stays private:
- `uinputpad` — `deck/deckpad.py` + `deckpad.service` + `verify_pad.py`, renamed
  (drop the "ShadowCast Virtual Pad" string), README + one curl example.
  **Apache-2.0.**
- `screenpad-mcp` — `mcp/shadowcast_mcp.py`, with the frame API documented as a
  **contract** (`GET /status`, `GET /frame?w=&q=`) so anyone can back it with OBS,
  a webcam, or their own capture app. Ship a 30-line reference frame server so it
  is usable without the private viewer. **Apache-2.0.**
Layout: two repos, not a monorepo — separate audiences, separate lifetimes, and a
monorepo drags the viewer's issues onto the small pieces.
**Rationale.** These are where reuse actually lives, and their maintenance surface
is genuinely tiny (stdlib-only, stable ABIs, no hardware prerequisite for the pad).
They also carry the most *current* story — "a model watches a console screen and
presses buttons" — which is a far better 2026 signal than "capture viewer".
Apache-2.0 rather than MIT for the patent grant and the inbound-contribution
clause, both of which matter if a company ever vendors it.
**Expected effect.** Two durable, citable artifacts; a plausible route to the only
contributions this project could realistically attract; zero exposure of the
component that carries the burden.
**IMPACT 4 · CONFIDENCE 4 · EFFORT S-M**
**Falsification test (<1 day).** Search GitHub/PyPI for existing "uinput HTTP
gamepad daemon" and "MCP screen capture / game input server" projects. If 3+
maintained equivalents already exist with meaningful traction, differentiation is
nil — publish only the README-level *findings* (why Bluetooth and USB-C
host-to-host are dead ends) and skip the code.

### P3 — Publish the viewer, renamed, source-only, GPL-3.0 + CLA
**Recommendation.** Rename off "ShadowCast" to a capability name. Publish
`native/` + `mf-viewer.html` under **GPL-3.0-only**, excluding `mpv/` and
`_retired-mpv/`. **No release binaries** (source + `dotnet publish` instructions).
Issues disabled, Discussions on, explicit no-support README, cla-assistant on any
code PR so dual-licensing survives. Widen device matching so it works with any
UVC dongle, not just a name containing "shadowcast" — otherwise the rename is
cosmetic and the audience stays as small as Genki's installed base.
**Rationale.** This is the only path that actually harvests hardware-variety bug
reports, which is the one thing open-sourcing uniquely provides. GPL-3 blocks a
closed reskin of the highest-effort component; the CLA keeps the owner's own
commercial options alive; source-only + no binaries removes the SmartScreen tax
and filters reporters down to people who can build, who write far better reports.
**Expected effect.** Modest adoption (build-it-yourself is a hard filter), a
handful of genuinely informative cross-hardware reports, ongoing low-grade
attention cost. Highest variance item on the slate.
**IMPACT 3 · CONFIDENCE 3 · EFFORT M**
**Falsification test (<1 day).** Size the addressable population before writing a
line: count distinct commenters on the "Arcade discontinued" threads and the
active membership of Genki dongle communities. If the reachable orphaned
population is a few hundred and mostly non-technical, a source-only repo will
serve almost nobody — do P1+P2 and stop.

### P4 — Keep everything private, deliberately, with a dated review
**Recommendation.** Status quo, but *chosen* rather than defaulted: repo stays
private, and the decision gets a written 90-day review date. Pair with the free
hygiene that costs nothing today — rename the project, keep `mpv/` quarantined,
keep sole copyright — so every other option remains cheap to exercise later.
**Rationale.** Capacity is the binding constraint, and this is the only option
with strictly zero downside risk: no support tax, no abandoned-repo signal, no
trademark visibility, every monetization path open, and the MCP + input stack
retains option value as a differentiated private capability. "Keep it private"
deserves to be beaten, not dismissed — but note it is *dominated by P1*, which
also costs no ongoing capacity and adds real upside.
**Expected effect.** Zero cost, zero upside. The correct baseline against which
everything else must justify itself.
**IMPACT 2 · CONFIDENCE 5 · EFFORT S (zero)**
**Falsification test (<1 day).** For one week, log every moment you would have
benefited from an outsider: a bug you cannot reproduce, a dongle/GPU/OS you do not
own, a question about whether a finding generalizes. Zero entries ⇒ staying
private costs nothing measurable and P3 is unjustified.

### P5 — Upstream the generalizable findings instead of owning them
**Recommendation.** Take the findings that are *not* specific to this hardware and
push them into projects that already have maintainers and audiences: an
mpv/ffmpeg documentation issue clarifying that `rtbufsize` is a latency ceiling
and not merely overflow protection; a note where the WinGet `Links` app-execution-
alias trap bites `Process.Start` with redirected streams; an OBS/capture-community
post on MJPEG-beats-raw at 1080p60 over USB3 with the jitter table. Attribution
under his own name, linking back to the P1 writeup.
**Rationale.** Converts know-how into reach and credibility while **someone else's
maintainers absorb the ongoing burden**. It is the highest reach-per-maintenance-
hour move available and it is routinely overlooked in favor of starting a repo.
It also compounds P1: an accepted upstream doc change is a stronger credential
than a personal blog post making the same claim.
**Expected effect.** Durable third-party-hosted references; incidental discovery
by exactly the people who would otherwise file issues at you.
**IMPACT 3 · CONFIDENCE 3 · EFFORT S**
**Falsification test (<1 day).** File exactly ONE such note (the rtbufsize
semantics one is the crispest and most checkable). If a maintainer engages within
a day, the lane is open; if it is ignored, or the behavior turns out to be already
documented, close the lane and fold the content back into P1.

---

## 3. Ranking by expected value

| # | Proposal | I | C | E | Why here |
|---|---|---|---|---|---|
| 1 | **P1 Knowledge-first writeup** | 4 | 5 | S | Highest value density on the slate: captures the scarce, unforkable asset, zero maintenance, forecloses nothing, and doubles as the demand test for everything else. |
| 2 | **P2 Spin out uinputpad + screenpad-mcp (Apache-2.0)** | 4 | 4 | S-M | Real reuse, best current-relevance signal, genuinely tiny support surface, viewer stays private. |
| 3 | **P5 Upstream the generalizable findings** | 3 | 3 | S | Reach without ownership. Compounds P1; cheap to test, cheap to abandon. |
| 4 | **P4 Stay private (deliberate baseline)** | 2 | 5 | S | Zero risk, zero upside. Correct fallback if P1's test fails; dominated by P1 otherwise. |
| 5 | **P3 Publish the viewer (GPL-3.0 + CLA, source-only)** | 3 | 3 | M | The only path that harvests cross-hardware reports, but it concentrates essentially all the maintenance risk of the whole program in one artifact, against ~zero capacity. Do it only after P1's demand test comes back strongly positive — and never with release binaries. |

Sequencing note: P1 → (test) → P2 → P5 → reassess P3. The order is deliberate —
each step is cheap, each generates evidence for the next, and none of the early
steps forecloses a later one. The reverse order (repo first, writeup later) spends
the maintenance budget before learning whether anyone wants the thing.

---

## 4. The single recommended default action

**Write and publish one technical writeup, under his own name, with a neutral
non-Genki title, covering the latency measurement method and the hard-won
findings — and keep all code private for now.**

One evening. No ongoing maintenance. It captures the only genuinely scarce asset
in the project (the measurements and the falsified hypotheses — the code is
replaceable, the numbers are not), it delivers the career signal the repo would
not, it reaches the orphaned owners while that window is still open, and it
forecloses no license, no repo and no monetization decision.

If he wants a second thing: rename the project off "ShadowCast" before anything
becomes public. It is free today, expensive after the first link is shared, and it
is what stops him from inheriting Genki's abandoned support desk.
