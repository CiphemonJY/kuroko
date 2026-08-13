# Council slate — LEGAL, IP & COMPLIANCE

Reviewer lens: software licensing obligations, trademark, redistribution terms, platform ToS,
content/DMCA exposure, liability. Read-only review of `C:\Users\james\Workbench\tools\shadowcast\`
as it stood 2026-08-06. **Rev 2** — incorporates the coordinator's four factual corrections (all
independently re-verified here, see §0.5) and withdraws one unverified premise from the trademark
analysis. Every conclusion below rests on a live read of the code this session, not on the brief.

**Not legal advice.** I am not a lawyer and neither is the reader. This is a risk assessment
written to bound the other three seats' options. Two items below (trademark clearance, a paid-product
EULA/privacy notice) genuinely warrant an hour of a real IP/tech-transactions attorney's time before
money changes hands; everything else is engineering discipline you can execute yourself.

---

## 0. The distinction that answers most of the questions

Almost every obligation in this review is triggered by **conveying** (distributing, publishing,
selling, uploading a repo), not by **using**. Concretely:

| Regime | Triggered by | Status for private use on James's box |
|---|---|---|
| GPL / LGPL / MIT copyright licenses | conveying a copy to another person | **zero obligation, forever** — GPL §0 explicitly does not restrict running |
| Trademark (Lanham Act) | "use in commerce" — distributing, advertising, a public repo, a store page | **zero** — internal naming is not use in commerce |
| DMCA §1201(a)(2)/(b) anti-trafficking | offering/providing to the public | **zero** — and nothing here circumvents anyway |
| DMCA §1201(a)(1) act of circumvention | the act itself, by anyone | **zero** — no access control is circumvented (see §1.6) |
| Product liability / CRA / GDPR / consumer law | placing on the market; processing others' data | **zero** for solo use |
| **Game & platform ToS** | **use, right now** | **LIVE ALREADY** — the only category active today |

So: the "should we sell / open-source it" decision is what creates the legal work. There is currently
one live issue and it is a ToS/account issue, not a liability issue.

---

## 0.5 Bill of materials for the SHIPPED artifact (verified, not assumed)

The coordinator's brief contained four factual errors that all pointed the same way — it conflated the
shipped app with a retired mpv-based prototype. I had independently caught three of them during my own
read and have re-verified all four. **This section is the corrected foundation for everything below.**

| Component | In shipped artifact? | Licence | Conveyed by you? | Obligation |
|---|---|---|---|---|
| Your own C#/HTML/JS/Python | yes | yours to choose | yes | pick one; add notices |
| .NET 9 runtime + libs (self-contained) | yes, embedded in the 109 MB exe | MIT (+ permissive) | **yes** | attribution notice |
| WebView2 SDK assemblies | yes | Microsoft SDK licence, redistributable incl. commercial | **yes** | notice; distribute unmodified |
| WebView2 Evergreen Runtime | no (assumed present on the OS) | MS terms, redistributable | not currently | none unless you ship the bootstrapper |
| **ffplay.exe (Gyan full_build, GPLv3)** | **NO — discovered at runtime** | GPLv3 | **NO** | **none** |
| ffmpeg.exe | **NO** — dev/retired scripts only | GPLv3 | no | none |
| AMD FidelityFX CAS GLSL | **NO** — `mpv/shaders/cas.glsl`, retired path | MIT | no | none |
| mpv, its config, `record.lua` | **NO** — retired path | GPLv2+ / LGPL | no | none |

**Verified this session:**
1. `native/Program.cs` invokes **ffplay only** (`FindTool("ffplay.exe")` at :722, `Process.Start` at :753).
   I grepped every `.cs` file for `ffmpeg|nvenc|libx264|h264_` — **zero matches**. Correction accepted.
2. Recording is in-page `MediaRecorder` with `['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus',
   'video/webm']` (`mf-viewer.html:576-578`). **VP9/VP8 + Opus, royalty-free. No NVENC, no H.264, no AAC**
   in the shipped path. Correction accepted — and it improves the codec-patent picture (§1.1).
3. Shipped sharpening is a stock 3x3 `<feConvolveMatrix order="3" preserveAlpha="true" divisor="1">`
   (`mf-viewer.html:135-136`) — an original SVG filter, no AMD code. The only residue is the DOM
   `id="cas"`, an internal identifier that is never user-visible and is not trademark use; rename it in
   thirty seconds if you want zero echo. Correction accepted.
4. `native/publish/` contains `ShadowCast.exe`, `ShadowCast.pdb`, three WebView2 `.xml` doc files, and
   `web/`. **No third-party binaries.** `ffplay.exe` (223 MB, at the WinGet packages path) is discovered
   at runtime and the app degrades to video-only when absent (`Program.cs:723`). Correction accepted.

**I agree with the coordinator's inference, and state it plainly: the shipped artifact carries no
GPL-derived code, no AMD-derived code, and no bundled third-party binaries. Your outbound licence choice
is therefore FREE — nothing in the codebase forces copyleft.** The remaining licence obligations are
attribution-only (MIT/.NET, WebView2 SDK), and they are satisfied by a text file. This is a materially
better position than the brief implied, and it is worth the other seats knowing it: **copyleft does not
constrain the monetization or open-source strategy at all.**

---

## 1. Risk register

Severity key: **BLOCKER** = do not release in that mode until fixed · **MUST-FIX** = fix before that
release mode · **MANAGEABLE** = accept with a mitigation/disclosure · **NON-ISSUE** = verified clean.

### 1.1 FFmpeg / ffplay — NON-ISSUE as built; only bundling would change that

**The single most important legal fact in this review: you do not convey FFmpeg, so no FFmpeg obligation
applies to you in any release mode.**

**Verified facts (read this session, not assumed):**
- The shipped `ShadowCast.exe` invokes **`ffplay.exe` only**, via `Process.Start` with
  `UseShellExecute=false` (`native/Program.cs:722-753`), after *discovering* it under
  `%LOCALAPPDATA%\Microsoft\WinGet\Packages` or PATH (`FindTool`, :825-852). No download, no bundle,
  graceful video-only degradation when absent. `ffmpeg.exe` appears only in `measure-latency.ps1`,
  `_retired-mpv/viewer.ps1`, and `mpv/scripts/record.lua` — dev tooling and the retired path, not the
  product. (Those scripts *would* ship with a public repo; see (d).)
- The binary it finds is the winget **Gyan.FFmpeg "full_build"**. I ran `ffplay -version`:
  `configuration: --enable-gpl --enable-version3 ... --enable-libx264 --enable-libx265 --enable-frei0r
  --enable-libvidstab ...`. That is **GPL v3**, definitively — not LGPL. There is **no
  `--enable-nonfree`**, so it *is* legally distributable, which is the good news. (A `--enable-nonfree`
  build would be undistributable at any price; worth knowing you dodged that.)
- The binary is 223 MB — larger than the app it would be bundled into.

**Does merely REQUIRING a user-installed GPL binary change anything for (b) free, (c) paid,
(d) open-source? No — in none of the three.** This is the question the coordinator asked and it deserves
a direct answer:

- The GPL is a **copyright** licence. Copyright obligations attach to **copying and conveying**. If you
  never transmit a copy of ffplay, you never exercise any right the GPL governs, and its conditions
  simply never engage. GPLv3 §0/§2 say this outright: *"This License explicitly affirms your unlimited
  permission to run the unmodified Program."* The user downloads FFmpeg from Gyan under **their own**
  licence grant, directly from the upstream distributor. You are not in that chain.
- **Charging money does not change it.** Whether your app is free, $5, or $500, you are conveying *your*
  code, not FFmpeg's. There is no "commercial use" trigger in the GPL — that is a persistent myth and it
  should not be allowed to distort the monetization seat's options.
- **Open-sourcing does not change it either.** Your repo can be Apache-2.0, MIT, or proprietary-source-available
  while documenting a GPL dependency the user installs. Depending on GPL software you do not distribute
  imposes no reciprocal obligation whatsoever. (Contrast: bundling would; see below.)
- The only residual is the derivative-work question, and it does not bite here either: **separate
  executables communicating at arm's length via `fork`/`exec` and a command line are separate works** under
  the mainstream reading, including FFmpeg's own community position. The FSF's more aggressive "intimate
  communication" theory targets in-process linking and shared data structures, not
  `ffplay -i audio=... -af "adelay=100:all=1,aresample=async=1000"`. You pass a device name and a filter
  string on argv and read nothing back but stdout/stderr you drain and discard. **Confidence: high.**
  And critically — **even if that theory somehow won, the remedy would only matter if you were conveying
  the GPL binary. You are not.** The two defences are independent and stack.

**Bottom line for (a) require-only, today's design: NON-ISSUE for private use, free release, sale, and
open source alike. Do not disturb it.** Make it deliberate rather than accidental (R2): a first-run
prompt telling the user to `winget install Gyan.FFmpeg`, and a README line stating FFmpeg is **not
included** and is separately licensed by its authors under the GPL.

One caution worth naming because it is the easy way to lose this position by accident: **do not add an
auto-downloader or an installer that fetches FFmpeg.** A bundling *mechanism* muddies the clean "we
convey nothing" story for zero user benefit over a one-line winget command, and it is precisely the kind
of convenience feature that gets added later without anyone re-asking the legal question.

**(b) IF you were to bundle the binaries — the counterfactual:** GPLv3
does not forbid selling — that is the most common misconception and it is wrong; you may charge any
price. What conveying GPLv3 binaries *does* require:
1. Ship the **full GPLv3 text** with the product.
2. Provide the **complete corresponding source** for that exact build, or a **written offer** valid
   3 years to supply it (GPLv3 §6). In practice: mirror Gyan's source tarball + build script yourself.
   Pointing at gyan.dev is *not* sufficient — §6(d) network-hosting relief applies to source you host
   alongside the object code you offer.
3. Add **no further restrictions** (GPLv3 §10) — so your EULA's "no redistribution / no reverse
   engineering" clauses must be carved out for the bundled ffplay, and any DRM/licence-key gating must
   not cover it (§3 anti-circumvention disclaimer, §6 Installation Information / anti-Tivoization).
4. Preserve copyright notices and the "no warranty" disclaimer.

Note the asymmetry that makes bundling the only scenario worth worrying about: the derivative-work
question is unimportant while you convey nothing, but if a court found a combined work **while you were
bundling and selling**, the remedy discussion starts at "the whole product must be GPLv3" — existential
for a proprietary paid product. **Bundling is what converts a dormant theoretical question into a live
existential one.** That is the real argument for staying where you are, and for the LGPL fallback if you
ever cannot.

**Mitigation with the best risk/effort ratio:** if you ever need to bundle, bundle an **LGPL build**
(e.g. BtbN's `win64-lgpl` artifacts, built without `--enable-gpl`). Then even the combined-work theory
only demands LGPL §4 relinking ability, which a separate `.exe` satisfies trivially, and the derivative-work
question stops being load-bearing. Cost: verify dshow input + `aresample`/`adelay` survive (they are all
in the LGPL core — `libavfilter`'s aresample/adelay are not GPL-only, and dshow is a Windows core input).

**Codec patents (separate from copyright — do not conflate).** Corrected per the coordinator and
re-verified: the **shipped** recording path is `MediaRecorder` with
`['video/webm;codecs=vp9,opus','video/webm;codecs=vp8,opus','video/webm']` (`mf-viewer.html:576-578`) —
**VP9/VP8 + Opus, royalty-free**, AOMedia/Google cross-licensed, encoded by the WebView2 runtime rather
than by any binary you ship. **NON-ISSUE in all release modes.** No NVENC and no H.264 anywhere in the
product. The **retired** paths transcode to **`h264_nvenc` + AAC**
(`mpv/scripts/record.lua:22-23`, `_retired-mpv/viewer.ps1:233`). AVC and AAC are pool-licensed
(Via LA / Access Advance). For a *sold* product that encodes H.264, the pool question is real even though
NVENC's hardware licence rides with the NVIDIA driver. **Recommendation: keep recording on VP9/Opus (or
AV1) and do not promote the H.264 path in a commercial build.** Severity: **MANAGEABLE**, becomes
**MUST-FIX** only if you resurrect H.264 recording in a paid product.

### 1.2 Trademark — "ShadowCast" is the single biggest release risk

**BLOCKER for free public release, paid product, and public open-source alike. NON-ISSUE for private use.**

The facts are stark. "ShadowCast" is Genki's (Human Things, Inc.) hardware product name — literally the
device this software drives. The app is *named* `ShadowCast`, the exe is `ShadowCast.exe`
(`ShadowCast.csproj:8`), the window title is `ShadowCast` (`mf-viewer.html:3,146,193`), the virtual host
is `shadowcast.local`, the config dir is `%LOCALAPPDATA%\ShadowCast`, the MCP server identifies as
`"name": "shadowcast"`, and the deck pad announces itself to Steam as `"ShadowCast Virtual Pad"`.

**Nominative fair use does not save this.** The *New Kids on the Block* factors ask (1) is the product
identifiable without the mark, (2) is only so much of the mark used as necessary, (3) does anything
suggest sponsorship. Using a mark to *refer to* the owner's goods — "a viewer for the Genki ShadowCast 2"
— passes. Using it as **your own product's name** fails prongs 2 and 3 outright: that is trademark
*use as a mark*, not reference. And the aggravating facts all run the wrong way:
- Identical mark, **identical goods class** (software for that exact device), identical customers.
  This alone is dispositive. Same mark + same goods + same buyers is the strongest likelihood-of-confusion
  showing there is, under any circuit's multi-factor test.
- Free distribution does not help. "Use in commerce" under the Lanham Act includes distribution and
  advertising; you do not need to charge money.

**UNVERIFIED PREMISE — flagged, and my conclusion does not rest on it.** My draft originally treated
"Genki deprecated Genki Arcade" as an aggravating fact. Another seat reports `arcade.genkithings.com` is
still live and Genki Studio is still in the App Store, so the deprecation may be **Windows-only, or
simply wrong**. I have not verified it and I am withdrawing it as a load-bearing fact. Note that it cuts
**both ways, and both directions are bad for you**:
- *If Genki did abandon the Windows app*, confusion is worse — users would read your product as the
  official replacement (an "initial interest confusion" pattern).
- *If Genki is actively maintaining the product line* (which now looks more likely), it is worse in a
  different and more practical way: **an active brand owner is far more likely to notice you and to
  enforce**, has live goodwill and an ongoing product to protect, and faces none of the abandonment
  or naked-licensing defences that a dormant mark invites. An actively-used mark is a *stronger* mark.

Either way the answer is the same, which is why the rename recommendation is robust to this uncertainty:
**same mark + same goods + same customers is enough on its own.** Verify Genki's actual status during
the R1 trademark search anyway — not because it changes the recommendation, but because it changes how
urgently you should move.

**What is clean (verified):**
- The **icon is originally drawn** — `native/make_icon.py` procedurally generates a dark rounded square,
  teal screen, scanlines, play triangle, from primitives. **No Genki artwork, no trade dress copied.**
  Good; this removes the second-worst version of this problem.
- **In-code device-name matching is fine and should stay**: `AudioDevice = "audio=Digital Audio Interface
  (2- ShadowCast 2)"` (`Program.cs:150`) and `const isSC = d => /shadowcast/i.test(d.label)`
  (`mf-viewer.html:306`) are functional string matches against a DirectShow enumeration. That is
  quintessential nominative/functional use — you cannot open the device without naming it. Nobody has ever
  been sued for matching a device string.

**Realistic outcome if you ship as-is:** a cease-and-desist and a forced rename, forfeiting your download
count, URLs, reviews, and search ranking at the worst possible moment. Platform takedowns (GitHub, itch,
Steam, Microsoft Store) resolve on a bare trademark complaint with no adjudication. If it is a *paid*
product, accounting-of-profits and (for willfulness, once you have notice) enhanced damages and fees
enter the conversation. Rename now, while the cost is a find-and-replace.

### 1.3 WebView2 and the .NET self-contained runtime — MANAGEABLE, notice gap is the real defect

- **WebView2 SDK** (`Microsoft.Web.WebView2` 1.0.2903.40, `ShadowCast.csproj:22`): Microsoft's SDK licence
  permits use and redistribution of the distributable code in your application, **including commercial
  applications**. No royalty. **NON-ISSUE for all release modes.**
- **Evergreen Runtime**: Microsoft expressly grants the right to redistribute the **Evergreen Bootstrapper
  and Standalone Installer** with your app, free, commercial included, *provided you distribute it
  unmodified*. **NON-ISSUE**, with one operational note: the app today **assumes the runtime is present**
  (`Program.cs:411-412` just calls `CreateCoreWebView2EnvironmentAsync`). It is preinstalled on Win11 and
  serviced Win10, so this is a support problem more than a legal one — but a paid product that hard-crashes
  on a machine without it is a refund/chargeback problem. If you instead choose **Fixed Version**
  distribution, that is also permitted but you inherit the duty to ship security updates, which is a
  meaningful ongoing obligation for a sold product (and increasingly a regulated one — see §1.8).
- **.NET 9 self-contained** (`SelfContained=true`, `PublishSingleFile=true`): the .NET runtime and
  libraries are MIT (plus a few permissive components). Redistribution inside a self-contained app is
  expressly contemplated and universal. **NON-ISSUE** — *except* that MIT requires the copyright notice
  to travel with the copy.
- **The actual defect: there is not a single licence or notice file anywhere in the tree.** I searched:
  no `LICENSE`, no `NOTICE`, no `THIRD-PARTY-NOTICES`, and `ShadowCast.csproj` sets **no
  `Company`/`Product`/`Copyright`/`FileVersion`** metadata, so the 109 MB exe ships with blank
  authorship fields. Shipping today would convey MIT-licensed .NET code and Microsoft's WebView2
  distributables with **zero attribution**. This is a genuine, if easily cured, licence breach.
  **MUST-FIX before any distribution mode.** Cure = one file plus five csproj lines.
- Minor: `native/publish/ShadowCast.pdb` sits beside the exe. Not a licence issue; just don't ship symbols.
- **AMD FidelityFX CAS — corrected: NOT in the shipped product.** Verified: the shipped sharpening is a
  stock 3x3 `<feConvolveMatrix order="3" preserveAlpha="true" divisor="1">` (`mf-viewer.html:135-136`),
  an original SVG filter with no AMD lineage. The AMD-derived GLSL lives only in `mpv/shaders/cas.glsl`
  (retired path). **NON-ISSUE for the shipped artifact.** Two residues, both trivial: the SVG filter's
  DOM `id="cas"` is a naming echo, never user-visible and not trademark use (rename in thirty seconds if
  you want zero echo); and *if the repo goes public including `mpv/`*, `cas.glsl`'s `//!DESC` names
  "AMD FidelityFX CAS" — FidelityFX is MIT-licensed but the name is an AMD trademark, so either add an
  MIT attribution line or call it "contrast-adaptive sharpening". One line. See (d) in §4 on scrubbing
  the retired directories generally.

### 1.4 Xbox 360 USB IDs in `deckpad.py` — MANAGEABLE, not a blocker

`deck/deckpad.py:80` packs `0x03, 0x045E, 0x028E, 0x0110` into `uinput_user_dev` — BUS_USB, Microsoft's
USB-IF vendor ID, Xbox 360 controller product ID.

Assessment: **the risk here is lower than it looks, and there is a large body of tolerated precedent.**
- The USB-IF vendor-ID regime is contractual — it binds USB-IF members and adopters of the USB-IF
  Trademark Licence Agreement, and it governs *products placed on the USB bus and marketed as USB-compliant*.
  This device is **virtual, never enumerated on a physical bus, never certified, never sold as hardware**.
  The contractual hook is weak.
- The residual theory is Microsoft trademark / passing-off: a device asserting it *is* a Microsoft product.
  In practice **xboxdrv, x360ce, ViGEmBus, vgamepad, MoltenGamepad, and countless evdev virtual pads have
  advertised 045E:028E publicly for well over a decade** — ViGEmBus even ships a WHQL-signed Xbox 360
  emulation driver. There is no public Microsoft enforcement against any of them. That is not a legal
  guarantee, but it is strong evidence of tolerated practice.
- **What would change the analysis:** using the *word mark* "Xbox" or the Xbox logo in your UI, icon, store
  page, or marketing. That is direct trademark use and is the thing to avoid. Your device name string is
  already `"ShadowCast Virtual Pad"` (`deckpad.py:60`) — neutral, good (subject to the §1.2 rename).
- **Explicitly NOT a §1201 issue.** Spoofing a controller ID on a Steam Deck — a general-purpose Linux PC
  the user owns — circumvents no technological access-control measure. This would be a very different
  conversation if the target were a *console* with an accessory-authentication chip (Xbox One / Switch
  licensed-accessory auth); this design drives a PC, not a console, and contains no auth emulation.
  **Do not add console accessory-auth emulation.**

Cheap de-risking option if you want it to be a non-question: Steam's mapping comes largely from name +
layout via the SDL gamepad database. Test whether a neutral VID/PID still maps correctly (see R6 below).

### 1.5 Game automation, anti-cheat, and streaming — MANAGEABLE for single-player, HIGH if marketed for multiplayer

This is the **one category already live for private use**, because ToS bind the user on use, not on
distribution. But its consequence for private use is an account ban, not liability.

- **Steam platform itself: low risk.** The Steam Subscriber Agreement targets cheating and unauthorized
  modification of the *Steam client / game code*. An external device injecting controller events modifies
  nothing; Valve ships Steam Input, Steam Link, and Remote Play, which are architecturally the same idea.
  Your design does not touch Steam's process at all.
- **Individual game EULAs: this is where the exposure is.** Blizzard, Riot, Bungie, Nintendo's online
  terms, and essentially every competitive title prohibit bots, macros, automation, and unattended play.
  Using this on those titles risks account termination. For the **seller**, the case to know is
  *MDY Industries v. Blizzard* (9th Cir. 2010): the bot vendor lost on **tortious interference** and on
  **DMCA §1201(a)(2)** for circumventing Warden, even though the direct copyright theory failed on §117
  essential-step grounds. The lesson is precise: **selling** automation that defeats an anti-cheat, and
  **marketing** it for that purpose, is what produced liability — not the automation itself.
- **Kernel anti-cheat (EAC / BattlEye / Vanguard / FACEIT)** runs on the machine the *game* runs on. In
  the intended topology the game runs on the console or the Deck, and the pad is a local `uinput` device —
  detectable (a virtual 045E:028E device with no real HID backing is a known heuristic), and grounds for a
  ban. Again: ban risk, not liability risk, **until you sell it for that**.
- **Streaming**: an AI playing a game and broadcasting it sits in the same copyright posture as any Let's
  Play — publisher-tolerated, DMCA-able at will, and Nintendo in particular is aggressive. Twitch also has
  rules around misrepresenting content and unattended streams. **MANAGEABLE with disclosure.**
- **The product-defining line:** frame it as **local/single-player play, accessibility, testing, and
  demonstration**. Never as "AI grinds/farms/plays ranked for you." The framing is not cosmetic — under
  MDY and under §1201(a)(2)(C) ("marketed for use in circumventing"), **your marketing copy is
  evidence of your product's purpose.** A sold "AI plays online for you" service is **HIGH risk**; the
  same binary sold as a local automation and vision tool is **MANAGEABLE**.

### 1.6 HDCP / DMCA §1201 — NON-ISSUE as built (confirmed), with a hard do-not-add list

**Confirmed clean.** I looked for it specifically and found nothing: no key material, no KSV/DKS handling,
no EDID manipulation, no crypto of any kind, no HDCP string anywhere in the shipped code. The pipeline is
`getUserMedia` → MediaFoundation → a UVC device that simply does not produce frames for protected sources.
The dongle *declines*; the software never sees, let alone defeats, an access control. Under
§1201(a)(2)/(b)(1) the tool is not "primarily designed or produced for the purpose of circumventing," has
overwhelming commercially significant non-infringing use, and is not marketed for circumvention. All three
statutory prongs fail. **Non-issue for private use, free release, sale, and open source alike.**

Preserve that. **Features that MUST NOT be added** — each would independently convert this from a clean
tool into a §1201 trafficking device, which is *criminal* under §1204 when done for commercial advantage,
and for which **there is no fair-use defense on the trafficking prong**:

1. **Any HDCP key material** — device keys, KSVs, the leaked HDCP master key, or a key-derivation routine.
2. **Any path that makes protected content capture anyway** — HDCP-stripper firmware, an "HDCP off" toggle,
   HDCP repeater/downstream masquerade, or version-downgrade tricks.
3. **EDID spoofing to induce a source to send unprotected output.** This is the tempting one because it
   looks like a compatibility feature. It is not; its only purpose would be defeating the protection.
4. **Recommending, linking to, or bundling an HDCP-stripping splitter in the docs, README, FAQ, or store
   page.** §1201 covers one who "offers to the public, provides, or otherwise traffics in" — and even
   short of that, a README paragraph saying "buy this $25 splitter and Netflix works" is the single most
   damaging document you could author. It converts marketing copy into proof of purpose.
5. **Any marketing copy implying protected-content capture** — "capture Netflix / Disney+ / streaming
   apps", "works with protected content", "no HDCP problems". Say the opposite, in writing.
6. Any DRM-bypass helper aimed at streaming apps or console content protection.

Also do not add console **accessory-authentication** emulation (§1.4) — same statute, same trafficking prong.

### 1.7 Privacy — the most under-appreciated item for a commercial release

The product's core AI feature **transmits continuous screenshots of the user's screen to a third-party
model provider**. `mcp/shadowcast_mcp.py` `get_screen`/`get_screen_burst` pull JPEGs from the viewer and
hand them to whatever MCP client is attached (Anthropic, OpenAI, a local model — the server does not know
or care). Those frames can contain other people's gamertags, chat messages, voice-chat rosters, DMs, and
anything else on the captured HDMI signal.

- **Private use: NON-ISSUE.** It is James's screen and James's choice.
- **Free public release: MUST-FIX (disclosure).** A plain-language README/first-run notice: what is
  captured, where it goes, that it leaves the machine, and how to turn it off (`-NoApi` exists — say so).
- **Paid product: MUST-FIX (privacy policy).** GDPR/UK GDPR and CCPA/CPRA disclosure duties attach on
  placing it on the market; if screens can contain third parties' personal data you are arguably a
  controller for that processing. Also disclose that WebView2/Edge sends its own diagnostic data to
  Microsoft.
- Architectural note that helps you: `FrameServer` binds **`IPAddress.Loopback`** (`FrameServer.cs:43`) —
  correctly scoped, good decision, and worth stating in the privacy notice. Residual: it is
  **unauthenticated on loopback**, so any local process can read the user's screen. Low severity, but for
  a sold product a shared token is ten lines and forecloses the question.
- One defect worth naming: `Program.cs:419-422` auto-allows **Camera and Microphone for any
  `PermissionRequested`**, unconditionally. Today the WebView only ever navigates to `shadowcast.local`,
  so practical risk is ~0 — but there is no origin check, so any future navigation (a clicked link, an
  injected page) silently gets camera and microphone. Unconsented microphone access is disproportionately
  ugly legally (state two-party-consent wiretap statutes, CIPA §631/§632 in California) relative to the
  three-line fix of gating on `e.Uri`. **MUST-FIX before a paid release.**

### 1.8 Liability, product safety, and the unauthenticated network service

`deck/deckpad.py:213` defaults `--host 0.0.0.0` with **no authentication of any kind** — a service that
injects arbitrary controller input into a device created with root privileges, reachable by anyone on the
LAN. The docstring even advertises `curl -X POST http://<deck>:8792/press`.

- **Private use on a trusted LAN: MANAGEABLE.** (The `--host 127.0.0.1` option exists and is documented in
  the argparse help — the *default* is the problem.)
- **Any distribution: MUST-FIX.** Shipping a product whose default configuration is an unauthenticated
  remote-input service is the kind of known-defect posture that carries weight under the **revised EU
  Product Liability Directive** (which now expressly brings software within strict product liability) and
  the **Cyber Resilience Act** (whose vulnerability-handling and reporting duties for "products with
  digital elements" phase in through 2026-2027 for products placed on the EU market). Even ignoring the EU
  entirely, "we shipped it wide open by default" is a bad fact in any negligence framing and an easy one to
  remove. `deckpad.service` runs it on boot, which widens the window.
- **No EULA / no warranty disclaimer / no limitation of liability anywhere in the tree.** For a sold
  product, UCC Article 2 implied warranties of merchantability and fitness attach *unless conspicuously
  disclaimed*. **MUST-FIX before sale.** This is a template exercise, not a bespoke drafting exercise.
- **The binary is unsigned.** Not a legal obligation, but SmartScreen friction on a paid download is a
  refund generator, and Microsoft Store distribution requires signing/identity outright.

---

## 2. TOP 5 remediation proposals

EV = (Impact x Confidence) / effort weight (S=1, M=2, L=3). Listed in EV order.

### R1 — Rename the product; keep nominative reference only. **EV 25**
**IMPACT 5 · CONFIDENCE 5 · EFFORT S**

**Concretely:** pick a non-Genki mark (something referencing capture/latency/glass, not the dongle).
Change: `ShadowCast.csproj` `AssemblyName`/`RootNamespace`; `mf-viewer.html:3,146,193`; the
`shadowcast.local` virtual host and `%LOCALAPPDATA%\ShadowCast` paths; `shadowcast_mcp.py` `serverInfo.name`;
`deckpad.py:60` device name; the `.cmd` and shortcut titles; download filename prefixes at
`mf-viewer.html:563,585`. **Keep** the DirectShow device-name matches at `Program.cs:150` and
`mf-viewer.html:306` — those are functional and protected. Add one line to the README, About box, and any
store page: *"An independent viewer for the Genki ShadowCast 2 capture device. Not affiliated with,
endorsed by, or sponsored by Genki or Human Things, Inc. ShadowCast is a trademark of its respective owner."*
That sentence is textbook nominative fair use and it is the only place the mark should appear.

**Risk removed:** the highest-probability, highest-cost failure mode — a C&D or platform takedown after you
have accumulated users, downloads, reviews, and SEO. Also removes willfulness escalation (once you have
notice, damages get worse) and the "official replacement for the deprecated Genki Arcade" confusion story
that makes this case unusually strong for Genki.

**Cheap verification (< 1 day):** USPTO TESS/TSDR search for `SHADOWCAST` and `SHADOW CAST` in
International Class 9, plus an owner search on "Human Things" and "Genki"; check status/goods description
and whether any registration is live vs. abandoned. Then a knockout search on your 2-3 candidate new names
in Class 9 and a domain/GitHub/Steam-name availability check. ~2 hours, free. **Do the candidate-name
knockout search before you commit — a rename into a *different* live mark is a wasted rename.**

### R2 — Lock in the no-bundle posture; make "require, don't ship" an explicit architectural rule. **EV 20**
**IMPACT 4 · CONFIDENCE 5 · EFFORT S**

*Re-scored after the coordinator's correction. This is no longer a fix — it is preservation of a position
you already hold. That lowers Impact to 4 and raises Confidence to 5.*

**Concretely:** keep today's detect-and-guide architecture and make it deliberate rather than incidental.
(1) A first-run dialog: *"Audio requires FFmpeg. Install it with `winget install Gyan.FFmpeg`,"* linking
to ffmpeg.org, with the graceful video-only fallback `Program.cs:723` already implements. (2) A README
line: *"FFmpeg is not included. It is a separate program, licensed by its authors under the GPL, which
you install yourself."* (3) **Write the rule down** in `docs/legal-boundaries.md` alongside R3: *no
bundled GPL binaries, no auto-downloader, no installer that fetches FFmpeg.* (4) Exclude `mpv/` and
`_retired-mpv/` from any release artifact and from a public repo, or clearly mark them retired — they are
the only GPL/AMD-derived material in the tree. If a bundle ever becomes commercially necessary, bundle a
**non-GPL (LGPL) build** and ship the LGPL text, an unmodified-binary statement, and a source offer.

**Risk removed:** the way this position is actually lost — not by a decision, but by a convenience feature
added months later by someone who never asked the question. Bundling would newly trigger every GPLv3 §6
source-provision and §10 no-further-restrictions obligation, create a direct EULA/GPL conflict for a paid
product, and convert the dormant combined-work question into an existential one for a proprietary product.
Also avoids +223 MB on a 109 MB app.

**Cheap verification (< 1 day):** **the decisive checks are already done this session** —
`ffplay -version` prints `--enable-gpl --enable-version3` (so: GPLv3, and no `--enable-nonfree`), and
`ls native/publish/` confirms no third-party binaries ship. Remaining, only if bundling is ever
reconsidered: download BtbN's `ffmpeg-master-latest-win64-lgpl`, run `ffplay -buildconf` to confirm no
`--enable-gpl`, and smoke-test `-f dshow -i "audio=..." -af "adelay=100:all=1,aresample=async=1000"`.
~1 hour, decides the bundling question on evidence rather than argument.

### R3 — Write the §1201 do-not-add list and the marketing-copy guardrail into the repo. **EV 20**
**IMPACT 5 · CONFIDENCE 4 · EFFORT S**

**Concretely:** add a short `docs/legal-boundaries.md` (or a CONTRIBUTING section) enumerating §1.6's six
forbidden features, and — more importantly — a **positive statement** that ships with the product:
*"HDCP-protected sources will not capture. This is a property of the capture hardware. This software does
not, and will not, provide any means of capturing protected content."* Put it in the README, the FAQ, and
any store page. Add a PR checklist item. Reject feature requests for EDID spoofing and "HDCP fix" with a
pointer to that doc, in writing, in public.

**Risk removed:** the only genuinely *criminal-adjacent* exposure in the entire system (§1204 penalties
attach to §1201 trafficking for commercial advantage, with no fair-use defense on that prong). It also
pre-empts the far more likely practical harm: a well-meaning contributor or an enthusiastic marketing line
turning a clean tool into a circumvention device. Cost is one page; the asymmetry is enormous.

**Cheap verification (< 1 day):** grep the whole tree plus every draft store/README/social string for
`hdcp|netflix|disney|protected|strip|splitter|edid|key`. Current state, verified: **clean — zero hits in
shipped code.** Re-run as a pre-release gate. ~15 minutes.

### R4 — Add LICENSE, THIRD-PARTY-NOTICES, assembly metadata, and an in-app About panel. **EV 20**
**IMPACT 4 · CONFIDENCE 5 · EFFORT S**

**Concretely:** (a) a `LICENSE` for your own code — **Apache-2.0 is the better default here than MIT**
for two lens-specific reasons: §3's express patent grant with defensive termination (relevant given the
codec and gamepad-ID adjacency), and §6's explicit statement that the licence grants **no trademark
rights**, which reinforces R1. (b) A `THIRD-PARTY-NOTICES.txt` covering .NET (MIT + Microsoft's
THIRD-PARTY-NOTICES) and the WebView2 SDK and Evergreen Runtime terms — **that is the entire conveyed
set**, both permissive, both attribution-only. Add a short "external dependencies (not included)"
paragraph naming FFmpeg's GPLv3 and phrased to make clear you do not convey it. Add the CAS attribution
**only if `mpv/` ships** (see R2). (c) Fill in `Company`, `Product`, `Copyright`, `FileVersion`,
`InformationalVersion` in `ShadowCast.csproj`. (d) A one-click **About/Legal** panel in the hover menu
showing all of it plus the Genki non-affiliation line from R1 — for a distributed GUI app this is the
conventional and sufficient way to satisfy "notices must accompany the copy."

**Risk removed:** a real, current, cheap-to-cure licence breach (conveying MIT-licensed .NET and Microsoft
distributables with zero attribution), plus the blank-authorship exe that reads as unprofessional and
raises SmartScreen suspicion. Also the single most common finding in any acquirer/partner due-diligence
pass, if the monetization seat's plan ever leads there.

**Cheap verification (< 1 day):** enumerate what the exe actually contains
(`dotnet publish` output manifest / `deps.json`) and diff it against your notices list; open the .NET
THIRD-PARTY-NOTICES file and the WebView2 SDK licence text in the NuGet package and confirm each ships.
~2 hours.

### R5 — Commercial-release hygiene: auth the pad, gate the WebView permissions, EULA + privacy notice. **EV 8**
**IMPACT 4 · CONFIDENCE 4 · EFFORT M**

**Concretely, four items:** (1) `deckpad.py` — default `--host 127.0.0.1`, require an explicit
`--bind-all`, and add a shared-token header check on every POST; update `deckpad.service` accordingly.
(2) `Program.cs:419-422` — gate the camera/mic auto-allow on `e.Uri.StartsWith("https://<yourhost>.local/")`
and deny otherwise; optionally add a token to the loopback FrameServer. (3) An EULA with a conspicuous
warranty disclaimer, limitation of liability, an **acceptable-use clause disclaiming multiplayer/online
automation and putting game-EULA compliance on the user**, and an FFmpeg carve-out if you ever bundle GPL
code. (4) A privacy notice covering §1.7 — screen frames leaving the machine to a model provider, WebView2
diagnostics, and the off switch.

**Risk removed:** unauthenticated remote input into a user's machine (PLD/CRA and negligence exposure, and
a plainly bad fact in any dispute), silent camera/mic grants to unexpected origins (wiretap-statute
adjacency), implied warranties attaching by default under UCC Art. 2, and undisclosed transmission of user
screen content under GDPR/CCPA.

**Cheap verification (< 1 day):** from a third machine on the LAN,
`curl -X POST http://<deck>:8792/press -d '{"button":"a"}'` — if the Deck's character moves, the exposure
is demonstrated in ten minutes and the argument is over. Separately, point the WebView at an external URL
in a debug build and confirm whether a camera prompt is auto-granted. ~1 hour total.

### Runner-up (below the cut, but cheap)
**R6 — Test a neutral VID/PID on the virtual pad.** Set `deckpad.py:80` to `0x0000/0x0000` or an unassigned
pair, restart Steam, and check whether the pad is still recognized with the correct layout (Steam maps
largely by name + SDL gamepad DB). If it works, the Microsoft-VID question disappears entirely for ~1 hour
of effort. If it does not, keep 045E:028E — §1.4's tolerated-practice analysis is adequate cover and this
is not a blocker. **IMPACT 2 · CONFIDENCE 3 · EFFORT S.**

---

## 3. Ranked slate (expected value)

| # | Proposal | Impact | Conf | Effort | EV | Gates which release mode |
|---|---|---|---|---|---|---|
| R1 | Rename; nominative reference + disclaimer only | 5 | 5 | S | **25** | free, paid, open-source (BLOCKER) |
| R2 | Lock in no-bundle posture (require, don't ship) | 4 | 5 | S | **20** | preserves all modes |
| R3 | §1201 do-not-add list + marketing guardrail | 5 | 4 | S | **20** | all modes (cheap insurance) |
| R4 | LICENSE + notices + assembly metadata + About | 4 | 5 | S | **20** | any distribution |
| R5 | Pad auth, permission gating, EULA, privacy notice | 4 | 4 | M | **8** | paid (must), free (partial) |
| R6 | Neutral VID/PID experiment | 2 | 3 | S | 6 | optional everywhere |

R1 through R4 are all **effort S**. The entire legally-required pre-release program is roughly one focused
day of work plus a couple of hours of searching.

**Headline for the other three seats, revised upward after the coordinator's corrections: the legal
constraints on this system are smaller than anyone assumed.** The shipped artifact conveys only
permissive, attribution-only components (.NET MIT, WebView2 SDK). There is no copyleft in the product, no
bundled third-party binary, no patent-encumbered codec, and no DMCA exposure. **Your outbound licence
choice is unconstrained — copyleft does not bound the open-source seat's options, and nothing in the
licensing bounds the monetization seat's options either.** What is left is one rename, one text file, one
policy page, and (for a paid product only) a standard EULA and privacy notice. Three of the four
"scary" items in the original brief evaporated on inspection; the one that did not is the product's name.

---

## 4. BOTTOM LINE

**(a) Private use — nothing is legally required.** Copyright licences bind on conveying, trademark on use
in commerce, §1201 trafficking on offering to the public; none is triggered by James running this on his
own box, and the design circumvents no access control. The only live exposure today is **game/platform
ToS**, which binds on use and whose worst outcome is an account ban — keep the AI-input side off online
multiplayer and that shrinks to nothing. **(b) Free public release — two things are actually required, and
both are cheap:** rename off "ShadowCast" (R1, the one true blocker — identical mark, identical goods,
identical customers, so nominative fair use cannot save a product *named* after the device maker's
product), and ship LICENSE + third-party notices (R4 — currently a real if trivial breach, since the exe
conveys MIT-licensed .NET and Microsoft distributables with blank authorship fields and no notice file
anywhere in the tree). Then two cheap prophylactics: the §1201 written boundary (R3) and a plain-language
notice that the AI features send screen frames off the machine. **Note what is NOT on this list: FFmpeg.**
Because you discover ffplay on the user's machine rather than shipping it, you convey no GPL code, so no
GPL condition engages — in this or any other release mode. **(c) Paid product — all of the above, plus**
an EULA with a conspicuous warranty disclaimer, limitation of liability, and an acceptable-use clause
pushing game-EULA compliance onto the user; a real privacy policy (the product transmits the user's screen
to a third-party model provider — that is the compliance fact people miss, and it is the one I would not
ship a paid product without); authentication on `deckpad`'s default-open `0.0.0.0` service and
origin-gating on the WebView2 camera/mic auto-allow; and code signing. **Charging money triggers no
additional licence obligation** — there is no "commercial use" clause in the GPL, MIT, or the WebView2
terms, and that misconception should not be allowed to distort the architecture. The only way a paid
product acquires a copyleft problem is by *bundling* FFmpeg, which is why R2 is written as a standing rule
rather than a one-time fix. **(d) Open-source — your licence choice is completely free.** Nothing in the
shipped code is copyleft-derived, so there is no forced reciprocity; prefer **Apache-2.0** over MIT for its
express patent grant and its explicit §6 statement that no trademark rights are granted (which reinforces
R1). Two caveats specific to going public: the **rename is still required** — a public repo is use in
commerce, GitHub honours trademark complaints, and neither MIT nor Apache-2.0 conveys any trademark right;
and the repo would publish `mpv/` and `_retired-mpv/`, the only GPL- and AMD-derived material in the tree
(retired mpv config, `record.lua`'s `h264_nvenc` transcode, `cas.glsl`), so exclude or clearly mark them.
Open-sourcing also makes R3 *more* load-bearing, not less: contributors will propose EDID spoofing and
"HDCP fixes" precisely because the repo is public, and a written, public do-not-add policy is what lets
you decline them on the record. **Get an actual attorney for two narrow things only: trademark clearance
on your new name, and a single review pass over the EULA and privacy policy before you take money.**
Everything else here you can execute yourself this week.
