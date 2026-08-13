# Council slate — MONETIZATION & ECONOMICS

Seat: revenue models, pricing, cost structure, market size, realistic income, opportunity cost.
Date: 2026-08-06 · Mandate: read-only · Artifacts reviewed: `mf-viewer.html` (683 ln),
`native/Program.cs` (871 ln), `native/FrameServer.cs` (148 ln), `native/ShadowCast.csproj`,
`mcp/shadowcast_mcp.py` (337 ln), `deck/deckpad.py` (233 ln), `memory/shadowcast_viewer.md`.

Other seats cover PMF, OSS strategy, and legal/IP. Where those overlap (trademark, GPL) I price
them as **cost lines only** and defer the substance.

---

## 0. Headline

**Direct product revenue here is ~$0, and a paid version is net-negative after costs.**
The only paths with a four-figure number attached bill the *know-how by the hour*, not the software.
The strongest recommendation in this slate is a **deliberate "monetize nothing"** decision, made
explicitly rather than by drift, plus two cheap lottery tickets (a Genki email, a writeup) that cost
hours rather than weeks.

The decisive evidence is in §1.3: the measured population of people who will seek out and install a
third-party ShadowCast viewer is roughly **10² – 10³ humans, all-time, all-platform**.

---

## 1. Lens review — is this monetizable at all?

### 1.1 What was actually built, priced honestly

Five components. Their *engineering* quality is not in question; their *economic* quality is:

| Component | Lines | Replacement cost for a competent dev | Verdict |
|---|---|---|---|
| WebView2/MF viewer + glass UI | ~1,550 | 1–2 weekends | **Commodity** |
| Audio split-out (ffplay + resample) | ~200 | 1 day *once you know the answer* | Commodity, hard-won |
| Local frame API (`FrameServer.cs`) | 148 | 2 hours | **Commodity** |
| MCP server | 337 | 3 hours | **Commodity** |
| `deckpad.py` uinput gamepad | 233 | 4 hours (`vgamepad`/`evdev` exist) | **Commodity** |
| **The measured findings** | — | **~20 hours of instrumented experiments** | **The only scarce asset** |

Read that table as a monetization statement: **~2,500 lines of a ~2,900-line system are commodity.**
The scarce thing produced this session is not code, it is a set of *empirically settled questions* —
`rtbufsize` is the latency knob and 24 MB is the right value; MJPEG beats raw because raw silently
stalls 228 ms and loses 1.6 % of frames; the periodic pop is a 44.1 k→48 k drift correction that page
code provably cannot reach; DirectShow's legacy shim is the hidden latency and MediaFoundation is why
Arcade felt snappier; `PrintWindow + PW_RENDERFULLCONTENT` because gdigrab returns black.

**Knowledge is not billable as a license.** It is billable as an hour, or spendable as reputation.
That single distinction determines this entire slate.

### 1.2 Where value concentrates — and it is not the viewer

- **The viewer: zero defensibility, and the category price is $0.** It competes with `shadowcast-electron`
  (free, MIT-ish, GitHub + Flathub, built for *this exact dongle*), `VideoGameCapture` (free, GPLv3,
  itch.io), OBS (free), and Genki's own browser app at `arcade.genkithings.com`, which still resolves.
  A paid utility entering a category with three established free options and a $50 host device has no
  pricing power. None.
- **The MCP/agent layer: novel-ish, but 337 lines and no moat.** Anyone with the dongle and an
  afternoon reproduces it. There is no data asset, no network effect, no switching cost, no
  distribution advantage. Novelty decays to zero the week someone blogs it.
- **The Steam Deck input bridge: structurally self-defeating (see §2.4).** The one platform where
  input is legally and technically possible is the one platform that *does not need a capture card* —
  on a Deck you can screenshot and inject input in software, locally, for free.
- **The know-how: the real asset, and it is illiquid.** Worth $100–200/hr to the right buyer and $0
  to a marketplace.

### 1.3 THE BOUNDING MEASUREMENT (do not skip this)

I checked the demand signal directly rather than modelling it:

> **`nicokimmel/shadowcast-electron`: 24 stars, 5 forks, 2 watchers, 1 open issue, 43 commits.**

That is a free, working, Flathub-distributed third-party viewer for this exact hardware, and over
its whole life it has accumulated **24 stars**. For a niche developer utility, stars typically run
1–5 % of actual users, giving **~500–2,400 lifetime users worldwide, all platforms**.

Contrast with the install base: ShadowCast 1 raised **$1.93 M from 30,382 backers**; the ShadowCast 2
"Alpine" campaign **$791 k from 5,532 backers**; a ShadowCast 3 campaign exists. So ~36 k Kickstarter
backers, plausibly 150–400 k lifetime retail units.

**The gap between ~250,000 owners and ~1,000 alternative-viewer seekers is the whole story.** Dongle
owners do not go looking for third-party software. They use what shipped, or the dongle goes in a
drawer. Any revenue model that multiplies by "install base" is off by two to three orders of magnitude.

One genuine nuance in this project's favour: `shadowcast-electron` ships prebuilt **for Linux only**
(Windows/macOS users must build it themselves), and its own README pitches itself as an alternative
to the Chrome app and "the Windows Store" — implying the orphaned Windows users are indeed underserved.
That is a real product gap. It is also a real product gap in a market of about a thousand people.

### 1.4 Two structural cost lines the other seats will raise, priced here

- **Trademark.** "ShadowCast" is Genki's mark, so it cannot be the name of a sold product. This is not
  merely a legal nuisance — it is an *economic* one: the single free acquisition channel available to
  a solo dev is people googling *"shadowcast viewer windows"*, and that is precisely the phrase a
  rename forfeits. **Renaming destroys the only free distribution the project has.** (Substance: legal seat.)
- **GPL binaries + code signing.** Shipping alongside ffmpeg/ffplay carries source-offer obligations
  (legal seat), and a 109 MB unsigned exe from an unknown publisher trips SmartScreen — which for a
  paid download is conversion death. An OV certificate is **~$200–400/yr**, and SmartScreen reputation
  realistically wants EV. **Any paid path starts the year several hundred dollars underwater.**

---

## 2. Proposals

Impact is scored as **expected contribution to the owner's 12-month income**:
**1 = <$100 · 2 = $100–1k · 3 = $1–5k · 4 = $5–20k · 5 = >$20k.**
Confidence 1–5. Effort S=1, M=2, L=3. EV = Impact × Confidence ÷ Effort.

Models explicitly considered and dropped before scoring, so the record is complete:
**Subscription** — nothing recurring is delivered; a local viewer has no server-side value to rent.
**Marketplace** — requires two-sided supply the project cannot generate.
**Dual-license** — presupposes a commercial licensee; there is no buyer, and the GPL dependency makes
the proprietary side of the dual awkward regardless.

---

### M1 — Monetize nothing: free release + a technical writeup as a reputation asset
**Model:** none (deliberate). Free binary + public writeup of the measured findings.

**Arithmetic.** Direct revenue **$0** by construction. The asset is the writeup: the memory file is
already 90 % of a strong post — the raw/MJPEG stall table, the `rtbufsize` latency result, the 44.1 k
pop diagnosis with registry evidence, the DirectShow-vs-MediaFoundation verdict. That is a genuinely
good post and good posts are how a solo engineer generates *inbound*. Value is option value on M2:
if a writeup reaches even 5,000 technical readers, a 0.1 % inbound-enquiry rate is ~5 conversations,
of which perhaps one becomes billable. Expected direct income **$0–1,000**, arriving as consulting,
not as sales.

Cost: ~3 hours (the draft exists). **Support burden: zero if released as-is with no promises.**
This is the option that keeps the asset and spends none of the runway.

Time-to-first-dollar: indirect, 1–6 months, possibly never.
**IMPACT 2 · CONFIDENCE 4 · EFFORT S · EV = 8.0**

**Falsification test (<1 day, and it doubles as the test for M4/M5):** post the writeup to Hacker News
and r/Genki / r/NintendoSwitch. Threshold: **100 upvotes or 1,000 views in 48 h.** Below that, there
is no audience for the free version, therefore certainly none for a paid one — and M4/M5 die with it.
This is the highest-information hour available anywhere in this slate.

---

### M2 — Consulting: bill the demonstrated capability (agent ↔ physical hardware) by the hour
**Model:** contract/consulting. The pitch is not "I made a viewer", it is *"I wired a vision model to
real hardware end-to-end and measured every stage of the latency budget: 17–41 ms frame fetch,
45–48 ms input round-trip, ~150–250 ms all-but-inference."* Very few people can produce that number
with receipts. Buyers: game-QA vendors, hardware/embedded test labs, accessibility orgs, robotics
teleop, retro/emulation companies, AI-eval outfits.

**Arithmetic.** Market rate for contract ML/systems work: **$100–200/hr**; game QA runs £25–75/hr for
manual testers, so a specialist automation rate at the top of that band is defensible.
Pipeline math: 50 targeted outbound messages → 5 % reply = 2–3 conversations → ~30 % close ≈ **1
engagement** → 40 h × $125 = **$5,000**. A second engagement in 12 months takes it to ~$10 k.

This is the **only proposal in the slate with a four-figure number that survives scrutiny**, and it
is 10–50× every product path. Its weakness is equally clear: it needs a sales pipeline the owner does
not currently have, and outbound is slow, unpleasant, and has a long tail to first dollar.

Time-to-first-dollar: **6–12 weeks minimum.** Ongoing burden: high while engaged (client work is
scope-creep by nature), zero between.
**IMPACT 4 · CONFIDENCE 2 · EFFORT L · EV = 2.67**

**Falsification test (<1 day):** send 10 specific, non-generic outbound messages describing the
capability with the latency table attached. Threshold: **≥1 substantive reply within 5 business days.**
0/10 = no pipeline exists at this price and positioning; kill or reposition. Cheap, and the reply
rate is the single most decision-relevant unknown in the whole slate.

---

### M3 — Pitch Genki directly (bounty / sponsored replacement / bundle)
**Model:** one-time B2B — bounty, contract rebuild, or "official Windows companion" bundled with the
dongle. The strategic logic is sound: Genki deprecated Arcade and orphaned tens of thousands of paying
backers, which is a live support cost and a live reputational cost to them. A working Windows viewer
that is *measurably faster than Arcade was*, built by someone who diagnosed exactly why Arcade felt the
way it did, is worth a conversation. It also resolves the trademark problem in the only way that
actually helps: with permission.

**Arithmetic.** Outcome distribution, honestly: 60 % no reply · 25 % "thanks, no" (possibly a free
dongle) · 10 % small bounty or one-time payment $500–2,000 · 5 % contract rebuild $3,000–8,000.
Expected value ≈ (0.10 × $1,250) + (0.05 × $5,500) ≈ **$400**.
Cost: **~2 hours** to write a good email with the evidence attached.
Effective rate ≈ **$200/hr expected** — the best ratio in this document, precisely because the cost is
so small. Note the ceiling is low and the outcome is out of the owner's control; do not build on it.

Time-to-first-dollar: 2–8 weeks if it lands at all. Support burden: potentially large *if* it becomes
an official companion — that would be a real obligation to real customers, and should be priced as a
contract (M2), never accepted as goodwill.
**IMPACT 2 · CONFIDENCE 3 · EFFORT S · EV = 6.0**

**Falsification test (<1 day):** one email to Genki partnerships/support (they run a support page and
a Discord). Threshold: **any human reply in 7 days.** Silence = closed. This test also returns the
trademark answer for free.

---

### M4 — Console/handheld QA automation as a service
**Model:** service (per-hour or per-build). Superficially the best-fitting market: automated regression
testing that reads the screen and drives a real controller, sold to indie studios. Market signal is
real — QA at £25–75/hr, indie fixed packages £5–15 k, and AI-assisted testing is a live category.

**Arithmetic, and why it collapses.** Input works **only on Linux/PC targets (the Steam Deck)**;
PS/Xbox need licensed auth chips with no path. So the addressable target is Steam Deck / Linux builds.
**But on a Steam Deck you do not need any of this.** You can capture the framebuffer and inject input
in software, on the device, for free, with lower latency and no dongle. **The hardware-in-the-loop rig
is redundant on exactly the one platform where it is permitted.** The residual market is "black-box
verification where instrumenting the device is forbidden" — real, but a handful of buyers worldwide,
none of whom are indie studios, and all of whom have procurement processes a solo operator cannot clear.

Add the multi-second VLM inference ceiling (cloud 1–4 s) and HDCP blocking protected content, and the
service cannot even promise coverage of a normal play session without the hierarchical policy work
that does not exist yet.
Realistic 12-month revenue **$0–3,000**, requiring L effort and a QA sales motion.

Time-to-first-dollar: 3–6 months. Support burden: very high (a service is an SLA).
**IMPACT 3 · CONFIDENCE 1 · EFFORT L · EV = 1.0**

**Falsification test (<1 day):** ask **one** Steam Deck/Linux game developer: *"would you pay for
hardware-in-the-loop screen-reading QA, given you could inject input in software on the device?"*
The predicted answer is "I'd just do it in software" — **which is why confidence is 1.** If three
developers all say that, this is a certified negative and should be logged as one.

---

### M5 — Paid one-time app (~$8, itch/Gumroad/Steam), with donations as the fallback
**Model:** one-time license, plus GitHub Sponsors / Ko-fi.

**Arithmetic, using the §1.3 anchor rather than the install base.**
Lifetime population that seeks a third-party ShadowCast viewer: **~500–2,400** (from 24 stars).
Windows share ~50 % → ~250–1,200. Reachable by a solo dev with no marketing budget, competing for the
search term he is not allowed to use → optimistically 50 %: **~125–600 people ever see the page.**
Conversion to *paid* when three free alternatives exist: **1–3 %.**

> **125–600 × 2 % × $8 = $20 – $96 LIFETIME gross.**

Even granting a 10× reach miracle: ~$200–960 lifetime. Now subtract:
code-signing **$200–400/yr** · platform fee **10–30 %** · payment/VAT handling.
**Net: negative in year one, and negative in most optimistic scenarios too.**

Donations fare no better: a utility with ~1 k users typically converts 0–5 sponsors at $3–5/mo;
the median GitHub Sponsors income for a project this size is **$0**. Call it **$0–200/yr**.

And the real cost is not money. Charging $8 converts a zero-obligation hobby artifact into an
**unbounded support liability** — every permutation of Windows audio driver, WebView2 version, GPU
vendor and dongle revision becomes the owner's problem, forever, for eight dollars. The support burden
is *inversely* related to the price. This is the worst trade in the slate.

Time-to-first-dollar: 2–4 weeks. Ongoing burden: **high and permanent.**
**IMPACT 1 · CONFIDENCE 4 · EFFORT M · EV = 2.0**
*(The EV formula flatters this by rewarding confident worthlessness. Net of the certificate and the
support tail, true expected value is **below zero**. Rank it last regardless of arithmetic.)*

**Falsification test (<1 day):** fake-door. Put up a Gumroad/itch page with the price and a Buy button
that leads to "not yet — notify me", before building any payment flow. Threshold over 72 h:
**≥1 % of visitors click Buy, and ≥25 absolute clicks.** Below that — the near-certain outcome — never
build the payment path. (Subsumed by M1's test: if the writeup cannot clear 1,000 views, this is moot.)

---

## 3. Ranking by expected value

| # | Proposal | Model | 12-mo revenue | I | C | E | **EV** |
|---|---|---|---|---|---|---|---|
| **1** | **M1 Monetize nothing + writeup** | none / reputation | $0 direct, $0–1k indirect | 2 | 4 | S | **8.0** |
| **2** | **M3 Pitch Genki** | one-time B2B | $0–5k (EV ~$400) | 2 | 3 | S | **6.0** |
| **3** | **M2 Consulting** | hourly | $0–10k (EV ~$5k if it lands) | 4 | 2 | L | **2.67** |
| 4 | M5 Paid app + donations | one-time / sponsorship | **negative** | 1 | 4 | M | 2.0 |
| 5 | M4 QA-as-a-service | service | $0–3k | 3 | 1 | L | 1.0 |

**Read the ranking correctly.** M1 and M3 rank top not because they earn much — they earn almost
nothing — but because they cost almost nothing and preserve optionality. M2 is the only real money and
it ranks third solely on effort; **if the owner wants income from this work, M2 is the answer and the
other four are noise.** The top-ranked plan is: *do M1 and M3 this week for five hours total, run M2's
test, and let the reply rate decide whether M2 gets any more runway.*

Total realistic 12-month expectation across every path: **$0 – $1,000**, with a low-probability
$5–10 k tail that exists only if the owner runs a sales process he has shown no appetite for.

---

## 4. OPPORTUNITY COST VERDICT

**Verdict: as an income lever, ShadowCast is a DISTRACTION. Take the five-hour version and stop.**

The owner's scarce resource is not ideas or compute — it is *his own attention*, and it is already
committed against three deadlines with real prize pools, all inside eleven weeks:

| Effort | Prize / target | Deadline | Status |
|---|---|---|---|
| Kaggriculture (Kaggle × Google) | **$50 k pool** | 2026-09-23 | entered, harness built, nightly search live |
| RSNA knee MRI | **$18 k** efficiency track | 2026-10-22 | extractor at 0.839 macro AUC |
| PTCG NAIC — Strategy track | cash | 2026-09-14 | writeup in progress |

Those are sunk-cost-advantaged: the work is largely built, the prize is denominated in five figures,
and a 5 % shot at $10 k is **$500 expected for marginal hours on an existing asset**. ShadowCast's
*entire* expected value across all five proposals is roughly the same $500 — but it requires
*net-new* work (a store page, a sales pipeline, a support obligation) in categories where the owner
has no distribution.

Three asymmetries seal it:

1. **The prize pools are already paid for.** Kaggle hours compound against a deadline with a
   guaranteed payout to *someone*. ShadowCast hours compound against a market of ~1,000 people who
   have already demonstrated they will not pay.
2. **Support is a liability that outlives the revenue.** Every ShadowCast monetization path except M1
   and M3 creates an obligation that keeps consuming attention *after* the money stops. A Kaggle
   submission ends on its deadline. This is the decisive structural difference.
3. **The deadlines are hard; ShadowCast has none.** Anything that can be done in November should not
   be done in August. M1's writeup and M3's email are the exceptions only because they take hours.

**Recommended allocation:** ~5 hours total, this week — publish free (renamed to clear the trademark),
post the writeup, email Genki. Then **stop and return to the competition deadlines.** Revisit only if
M2's outbound test returns a genuine reply, in which case consulting becomes a real conversation and
should be scheduled *after* 2026-10-22.

**What would change this verdict:** a reply from Genki proposing paid work; or ≥2 substantive replies
from M2 outbound; or the writeup clearing ~500 HN points, which would indicate an audience an order of
magnitude larger than measured and would justify re-running §1.3's arithmetic from scratch. Absent one
of those three signals, the correct amount of further monetization effort is **zero**.

**Finally — and this is not a consolation prize.** The build was not a mistake. It replaced a
deprecated tool the owner actually uses, it produced a genuinely reusable agent↔hardware capability,
and it generated ~20 hours of hard-won measured knowledge in one session. It was a good *engineering*
outcome. It is simply not a *revenue* outcome, and the honest thing is to book it as tooling and
portfolio rather than to spend three more weeks discovering that a thousand-person market will not
fund a support obligation.

---

## 5. Evidence

- `shadowcast-electron` traction (the bounding measurement): 24 stars / 5 forks / 2 watchers / 1 open
  issue / 43 commits — https://github.com/nicokimmel/shadowcast-electron ; Linux-only prebuilds,
  also on Flathub — https://flathub.org/en/apps/de.nicokimmel.shadowcast-electron
- Install base: ShadowCast 1 — 30,382 backers / $1,931,677
  (https://www.kickstarter.com/projects/humanthings/genki-shadowcast ;
  https://www.nintendolife.com/news/2021/01/genki_shadowcast_kickstarter_ends_with_almost_usd2_million_raised) ·
  ShadowCast 2 "Alpine" — 5,532 backers / $791,831
  (https://www.kickstarter.com/projects/humanthings/genki-alpine) · ShadowCast 3 campaign exists
  (https://www.kickstarter.com/projects/humanthings/trilogy)
- Free competitors in-category: OBS/Streamlabs (free) · `VideoGameCapture`, GPLv3
  (https://immernochnoah.itch.io/videogamecapture) · Genki Arcade still resolving at
  https://arcade.genkithings.com/
- QA rate benchmarks: £25–75/hr per tester; indie fixed packages £5–15 k
  (https://gamestudiounlocked.substack.com/p/qa-for-indie-game-studios ;
  https://testers-hub.com/game-testing-cost/)
- Prior art on the agent side: `lmgame-org/GamingAgent` (ICLR 2026) — VLM gaming agents, open source
  (https://github.com/lmgame-org/GamingAgent). Relevant because the *interesting* half of this project
  has a well-funded academic open-source competitor giving it away.
- Internal, measured (from `memory/shadowcast_viewer.md`, not re-derived): frame fetch 17–41 ms ·
  input round-trip 45–48 ms · all-but-inference ~150–250 ms · cloud VLM inference 1–4 s dominates.
