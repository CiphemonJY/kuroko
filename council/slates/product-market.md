# Council slate — PRODUCT & MARKET seat

**Scope:** applications, users, demand, competitive landscape, positioning.
Not covered here (other seats): unit economics/pricing, OSS-vs-closed strategy, trademark/legal/HDCP.
**Mode:** read-only. Nothing in the tree was modified. Market claims below are cited to live web
reads this session or to files; anything unverified is labelled so.

---

## 1. Lens review — what is real, what is commodity

### 1.1 The viewer is ~85% commodity, but the 15% is a real, widely-felt pain

Feature-by-feature, almost everything in `mf-viewer.html` is a checkbox OBS Studio already ships free:
screenshots (`mf-viewer.html:555`), `MediaRecorder` at 20 Mbps (`:578`), device selection (`:166`),
mute/volume, always-on-top, stats overlay, CRT scanlines (`:56`), CAS sharpening (`:134`). Recording,
per-app WASAPI volume (`native/AudioSession.cs`), and the Mica/glass borderless chrome
(`native/Program.cs:12-127`) are polish, not product. A reviewer comparing this to OBS feature-for-feature
concludes "why does this exist."

Three things are **not** commodity, and they are the entire differentiated surface:

1. **Preview latency.** OBS's own forums put capture-device→preview at **60–400 ms**, and note OBS is
   already *the best* of the available software ([OBS forum][obs1]). This tool measured 80 ms
   capture→screen and identified *why* (`shadowcast_viewer.md`: DirectShow's legacy shim buffers before
   any readable timestamp; MediaFoundation via Chromium does not). That diagnosis is the durable asset —
   it is the reason the tool is faster, and it is not obvious to anyone else in the space.
2. **The 44.1 kHz-only audio pin fix.** The dongle's audio pin is 44.1k-only, every Windows output is
   locked at 48k, and Chrome's resampler corrects drift in audible periodic *snaps*. The shipped fix
   (separate ffplay process, `aresample=async=1000`, `adelay` for the path-length skew —
   `native/Program.cs:144-149`) is the kind of thing nobody solves by accident. Cheap-dongle audio pops
   are a chronically-complained-about class of problem.
3. **Zero-install single exe.** 109 MB, no scene collection, no sources, no setup. For the "I just want
   to see my Switch on this laptop" user, OBS's setup is the product failure, not its latency.

**Prior art check.** This niche is not empty: `shadowcast-electron` is already on Flathub as a minimal
third-party ShadowCast viewer ([Flathub][fh]), and `elgato-live-preview` exists on GitHub for the same
job on Elgato hardware ([GitHub][elp]). Both are demand *evidence* and competition. Neither appears to
have solved the audio-clock problem.

**Positioning defect (my lens, not the legal seat's):** naming the tool after Genki's hardware
hard-couples a general-purpose utility to one vendor's accessory, and the device dropdown at
`mf-viewer.html:166` already makes it device-agnostic in code. The name is costing ~2 orders of
magnitude of addressable market for zero benefit. Rename before anything ships publicly.

**Flag on a briefed fact:** the brief states Genki deprecated Genki Arcade. Public surfaces still show
`arcade.genkithings.com` live and Genki Studio in the App Store ([Genki][gk], [App Store][as]). The
deprecation may be Windows-specific or partial. This matters: "rescue the orphans" is a much weaker
narrative if the app still works for Mac/iPad users. **Verify the exact scope of the deprecation before
building any messaging on it.**

### 1.2 The MCP + input layer is the genuinely novel thing — and its value is currently gutted by one fact

`mcp/shadowcast_mcp.py` + `native/FrameServer.cs` + `deck/deckpad.py` compose something no shipped
product does: a **hardware-level, engine-agnostic agent interface**. Video in through HDMI (no emulator,
no ROM, no API, no engine hook), input out through a real gamepad the target treats as genuine hardware.
Every existing "AI plays games" harness — ALE/Gymnasium, stable-retro, VizDoom, MineRL, VideoGameBench,
BALROG, Claude Plays Pokémon — is software-in-the-loop. This is not.

The engineering craft is above-average in exactly the place that matters. The MCP tool descriptions
(`shadowcast_mcp.py:83-208`) are written *for a model*, not copy-pasted from a REST doc:
`get_screen_burst`'s "a single frame cannot answer that", `move_stick`'s "0 (default) leaves it held —
remember to recentre or the character keeps walking", `release_all` existing as a crash-safety net
(`deckpad.py:129-138`), and `input_sequence` executing Deck-side so combo timing has no network
round-trip between steps (`:188-200`). That last one is a genuine latency insight. `FrameServer.cs`
existing *at all* because the device is exclusive (`:12-28`) is correct systems thinking.

**But — the Steam Deck paradox, which is the single most important finding in this review:**

> The only target platform where input works is the only target platform that does not need the
> capture hardware.

If the target is a Steam Deck / Linux PC, you can grab the framebuffer locally (gamescope, KMS,
`grim`/`wf-recorder`, or Steam Remote Play) and inject uinput locally. That path is cheaper (no $50
dongle), lower latency (no HDMI→USB→MediaFoundation→canvas→JPEG→HTTP chain), higher fidelity (no MJPEG
loss), and has *zero* of the exclusivity, 44.1 kHz, and rtbufsize problems documented across
`shadowcast_viewer.md`. Anyone technical evaluating the rig notices this within an hour. Per the binding
facts, PS4/PS5/Xbox have no input path at all. So today the agent stack is a **very well-built
demonstration of a capability that has no target it is the right tool for**. Proposal P4 is the only
thing on this list that fixes that, and it is the one I'd de-risk first with a 20-minute check.

### 1.3 Latency budget — what genres are actually reachable

150–250 ms of non-inference overhead is a hard floor even with a 10–50 ms distilled policy. Total best
case ~200–300 ms. That forecloses action games permanently and confines the agent to turn-based and
slow-paced content: RPGs, Pokémon, strategy, visual novels, farming/life sims, menu grinding, gacha
dailies. That genre set overlaps almost exactly with the genres people *already* automate via emulators
and macro tools — where they get save states, frame stepping, and RAM access for free. Honest read: on
reachable-genre grounds alone, the physical rig is a *worse* research platform than an emulator for
everything an emulator can run. Its only defensible territory is content an emulator **cannot** run.

### 1.4 Kaggle transfer — negative finding, stated plainly

There is none. PTCG and Kaggriculture are simulator-based agent competitions; a physical HDMI capture
rig contributes zero code and zero data to either. The one transferable item is the *hierarchical
slow-coach → fast-distilled-policy architecture*, which is a method already known to the program and
does not require this hardware to develop or validate. **Do not let "it's adjacent to the Kaggle work"
justify further time here.** If the goal is income via Kaggle, this project competes with that goal for
hours rather than feeding it.

---

## 2. Proposals

Scoring: IMPACT 1-5, CONFIDENCE 1-5, EFFORT S/M/L. EV = Impact × Confidence ÷ Effort (S=1, M=2, L=3).

---

### P1 — Device-agnostic low-latency HDMI passthrough viewer (**EV 12.0**)

**IMPACT 3 · CONFIDENCE 4 · EFFORT S**

**What / mechanism.** Drop the Genki-specific framing entirely. Ship the existing exe, renamed, as
*"the window that shows your capture card with the least delay and no audio pops, with no setup."*
The device dropdown already exists (`mf-viewer.html:166`); the only real work is removing the
name-matching assumption that refuses non-ShadowCast devices (`shadowcast_viewer.md`, "Traps" section)
and replacing it with a device picker that accepts any UVC source. Positioning is one sentence: *OBS is
a studio; this is a window.*

**Who wants it, and how many.** Not Genki owners — **anyone who bought a capture dongle to play a
console on a monitor/laptop they already own.** Cheap UVC HDMI dongles ($10–60) sell in the millions
annually across Amazon/AliExpress; the sub-segment that is playing (not streaming) through them and
therefore feels preview latency is the target. Order of magnitude: low hundreds of thousands of people
have hit this pain; a free tool might realistically reach 1k–50k of them. *Unverified — sizing is
exactly what the test below checks.* The Genki-orphan segment is a subset, not the market.

**Why I believe it.** OBS's own forum concedes 60–400 ms preview and that OBS is the best available
([obs1]). Two independent developers built single-purpose low-latency viewers for specific dongles
([fh], [elp]). That is three separate signals that "OBS is the wrong shape for this job" is a felt
problem, not a hypothesis.

**Weaknesses I'm not hiding.** Free-tool market, non-paying audience, support burden from users with
50 different no-name dongles, and the two best features (latency, audio) are invisible in a screenshot —
this is the hardest kind of product to market because the value only shows up when you use it.

**Cheap falsification test (<1 day, mostly zero-code):**
1. Pull the **Flathub install count for `de.nicokimmel.shadowcast-electron`** and the **GitHub star count
   for `simontreny/elgato-live-preview`**. These are direct demand proxies for "someone wants a
   single-purpose capture viewer." If both are in the low hundreds after years, the ceiling is a few
   thousand users and this is a hobby release, not a lever. If either is 5k+, the market is real.
2. Keyword volume check on "obs capture card latency", "capture card delay switch", "genki arcade
   alternative" (free keyword tool or Google Trends).
3. One post to r/consolecapture, r/OBS, and r/NintendoSwitch: *"I got capture-card preview down to ~80 ms
   and killed the audio pops — worth releasing?"* with the measured comparison. <20 upvotes / <5 comments
   across all three in 24 h ⇒ demand is not there.

---

### P2 — Content/demo flywheel: "an AI plays a real console, through real hardware" (**EV 9.0**)

**IMPACT 3 · CONFIDENCE 3 · EFFORT S**

**What / mechanism.** Claude Plays Pokémon proved a very large audience exists for LLM-plays-game
content. Every instance of that genre is an emulator. The visually legible novelty here is the
*physical* framing — a real console, a real dongle, a real controller moving. The asset produced is not
software; it's attention and inbound. Cost is one weekend plus a stream. This is a **top-of-funnel play,
not an income lever** — I'm ranking it high purely because it is the cheapest possible test of whether
anything about this rig is interesting to anyone but its author.

**Who / how many.** The AI-dev audience on X/r/LocalLLaMA/r/singularity (millions reachable, ~0% direct
monetization) plus gaming YouTube. Realistic outcome distribution is bimodal: nothing, or one viral clip.

**Dependency and honesty caveat.** On a Steam Deck this demo is *not impressive* — "AI plays PC game"
is already a saturated genre and viewers will correctly ask why the dongle is in the loop. The demo only
lands on a console you cannot emulate, which means **P2's real impact is gated on P4**. Run P2's test
anyway, because it is free.

**Cheap falsification test (<1 day):** post a 45-second clip with the physical hardware in frame to
r/singularity + r/LocalLLaMA + X. Calibrate first by checking view counts on the existing "AI plays
[game]" genre — if the *median* recent video is under ~10k views, the novelty window has closed and this
is over regardless of execution. <500 upvotes / <10k views in 24 h ⇒ spent.

---

### P3 — Screen-aware game *coach* (vision only, no input) (**EV 6.0**)

**IMPACT 4 · CONFIDENCE 3 · EFFORT M**

**What / mechanism.** Strip the input half. A VLM that can see any console's HDMI output and answer
"what do I do here", "what does this item do", "why did that fight go badly", "read this menu for me."
This is the **largest version of the agent story that survives every binding fact** — it needs no input
path, so it works on PS5, Xbox, Switch, and retro hardware alike. Everything needed already exists:
`FrameServer.cs` + `get_screen` / `get_screen_burst`. The latency budget is irrelevant because a coach
is conversational, not reactive.

**Who / how many.** (a) Stuck players broadly — enormous but very low intent to install anything;
(b) import/JP-language gamers on original hardware — tens of thousands, high intent;
(c) disabled gamers via AbleGamers/SpecialEffect channels — small, funded, high mission value, and the
"agent handles the section I physically can't" framing is genuinely differentiated.

**Major downgrade — the obvious sub-application is already a red ocean.** Live OCR/translation is
served by at least five active free tools: Translumo, RSTGameTranslation, Game-Changing Translator,
Thaluna, and Seth Robinson's Universal Game Translator — the last of which **explicitly targets original
consoles through capture cards** ([search results][ocr]). Do not build translation. The surviving
differentiation is the part OCR tools structurally cannot do: *conversational reasoning over game
state* ("I have 3 HP and no potions, what's my play"), which needs a VLM, not an OCR pipeline. That is a
narrower and much less proven want than translation.

**Cheap falsification test (<1 day):** post a 30-second screen recording of the model correctly
answering a non-OCR question about a live console screen (a tactical read, not text) to r/JRPG and
r/patientgamers, and post the accessibility framing to r/disabledgamers plus AbleGamers' community
channel. The signal to watch for is *"OCR tools can't do that"* in replies — if the top comments instead
say "Translumo already does this," the differentiation is imagined and this proposal dies.

---

### P4 — Unlock a real console input path via a USB-gadget SBC (**EV 5.0**)

**IMPACT 5 · CONFIDENCE 2 · EFFORT M**

**What / mechanism.** This is the keystone. Everything above is either commodity (P1), gated (P2), or
input-free (P3) because the rig cannot press buttons on a console. The binding facts correctly rule out
*Windows as a BT peripheral* and *USB-C host-to-host* — but they describe what the PC can do directly,
not what a dedicated intermediary can do. A Linux SBC with USB device-mode hardware (Pi Zero 2 W, Pi
Pico W, or the fleet's existing Jetson) presenting as a Switch Pro Controller is a well-established
mechanism with multiple live implementations: `nxbt`, `joycontrol`, `nxbt-plus`, `PicoSwitchController`,
`joyconpi` ([search results][nx]). If this works, the rig becomes "any Switch game, on real hardware,
seen and played by a model" — a genuinely new capability, and the thing that makes P2 and P3 land.

**Confidence is 2 for good reasons.** Switch firmware 12.0.0 broke *every* software controller emulator
simultaneously; the projects recovered partially, and none of the search results confirm current-firmware
status ([nx]). Nintendo has both motive and a track record here. This is a moving target maintained by
volunteers. **Route this proposal through the hardware and legal seats before any work — if either has
already certified it dead, drop it and re-rank P3 to the top.**

**Cheap falsification test (~20 minutes, zero code — do this FIRST, before anything else on this slate):**
1. Open the `nxbt` and `joycontrol` GitHub issue trackers and search for the current firmware version.
   If the newest confirmed-working report is >18 months old and issues read "doesn't pair anymore," it's
   dead — stop.
2. **The higher-value half:** check whether an off-the-shelf **Mayflash Magic-NS / 8BitDo USB receiver**
   accepts a *virtual* (uinput) gamepad as its input source and presents it to a Switch. These adapters
   explicitly take standard wired USB controllers and feed a Switch ([Amazon/Mayflash][mf]); the open
   question the search could not answer is whether a uinput device satisfies them. **If yes, the entire
   input problem is solved by a $20 adapter and the Steam Deck architecture was never necessary.** That
   is the single highest-value unknown on this slate, and it is a forum search plus one purchase away.
   Note the binding fact stands for PS5/Xbox — those adapters do not defeat current-gen auth.

---

### P5 — Hardware-in-the-loop QA / game-testing product (**EV 0.67 — recommend NOT pursuing**)

**IMPACT 2 · CONFIDENCE 1 · EFFORT L**

**Why it looks attractive and why it isn't.** "Automated smoke tests on retail console hardware with a
screenshot and video trail" sounds like a fundable wedge. It is not, and the market check is decisive:
**GameDriver already ships console test automation for PlayStation, Xbox, and Nintendo Switch**, and
**modl.ai already ships explicitly black-box, vision-only game testing** ([search results][qa]). The two
things this rig would sell as novel are each already an incumbent's shipped product, and both incumbents
are enterprise-sold with studio relationships and devkit access.

Worse, the technical premise is inverted for the actual buyer: a studio *owns its engine*, so
engine-hooked testing (GameDriver, Unity Test Framework, AltTester) gives ground truth where pixels give
inference. Pixel-only is strictly worse for the customer who has the choice. And the binding facts
remove the one case where pixels are unavoidable — no PS/Xbox input path means the rig cannot drive the
platforms studios most need certified.

The only surviving niche is testing something you *don't* own — e.g. third-party Proton/SteamOS
compatibility verification, which Valve does manually at scale for Deck Verified. That is a real gap but
it is a single-customer market, and that customer is Valve.

**Cheap falsification test (<1 day, only if someone insists):** DM five QA leads on LinkedIn with the
60-second demo asking "would you pay for pixel-only hardware-in-the-loop testing?" and request a
GameDriver/modl.ai quote to see whether the budget line exists at all. Expect the answer "we already have
this and ours sees the engine."

---

## 3. Ranking by expected value

| # | Proposal | I | C | E | EV | Verdict |
|---|----------|---|---|---|-----|---------|
| 1 | **P1** Device-agnostic low-latency viewer | 3 | 4 | S | **12.0** | Ship it; rename first |
| 2 | **P2** Content/demo flywheel | 3 | 3 | S | **9.0** | Free to test; gated on P4 for real impact |
| 3 | **P3** Screen-aware game coach (vision only) | 4 | 3 | M | **6.0** | Best *product* story; avoid translation |
| 4 | **P4** Console input unlock via USB-gadget SBC | 5 | 2 | M | **5.0** | Keystone; **run its 20-min test FIRST** |
| 5 | **P5** Hardware-in-the-loop QA product | 2 | 1 | L | **0.67** | **Do not pursue** — occupied market |

**Sequencing note — EV ordering is not action ordering.** The EV column favours cheap tests, which is
correct for the next 24 hours but not for the next month. The dependency structure says: **run P4's
20-minute adapter/firmware check before anything else**, because its outcome re-ranks the whole slate.
If a $20 adapter can take a virtual pad to a Switch, P4 collapses to EFFORT S, P2 becomes a real demo,
and the agent stack has a target it is genuinely the right tool for. If P4 is dead, the honest slate
shrinks to *"P1 is a good free tool, P3 is a maybe, and the input half was a well-built dead end"* —
and the Steam Deck paradox (§1.2) means the agent code should be recognised as a portfolio/demo artifact
rather than a product line.

## 4. The three findings I'd defend hardest

1. **The Steam Deck paradox.** The only input-capable target is the only target that doesn't need the
   capture hardware. Until input reaches a console, the agent stack is a capability without a market.
2. **P5 is an occupied market and P3's obvious use case is a red ocean.** Both were checked live this
   session; both incumbent sets already ship the differentiating feature. Better to know now.
3. **Zero Kaggle transfer.** This project competes with the stated income lever for hours; it does not
   feed it. The only crossover is a method (hierarchical coach → distilled policy) that needs no dongle.

---

[obs1]: https://obsproject.com/forum/threads/reducing-input-to-program-preview-latency.163272/
[fh]: https://flathub.org/en/apps/de.nicokimmel.shadowcast-electron
[elp]: https://github.com/simontreny/elgato-live-preview
[gk]: https://arcade.genkithings.com/
[as]: https://apps.apple.com/us/app/genki-studio/id6466343285
[ocr]: https://github.com/ramjke/Translumo
[nx]: https://github.com/Brikwerk/nxbt
[mf]: https://www.amazon.com/MAYFLASH-Bluetooth-Controller-Raspberry-Compatible/dp/B079B5KHWQ
[qa]: https://gamedriver.ai/
