# Design

## Context

See `proposal.md` for motivation and scope. Only the constraints that shape the approach are repeated here, and each one was measured on the target machine rather than assumed.

**Runtime facts (verified)**

- AstrBot 4.28.2 runs as a desktop bundle: `astrbot-desktop-tauri.exe` plus `D:\Astrbot\backend\python\python.exe launch_backend.py --webui-dir D:\Astrbot\webui`, listening on 6185 / 6199 / 6321 / 6322.
- Live data lives in `C:\Users\Rain zhou\.astrbot\data` (not `D:\Astrbot\data`, whose `cmd_config.json` is a stale empty template): `cmd_config.json` 24 KB, `data_v4.db` 5.4 MB + 5.2 MB WAL, 16 plugin folders.
- QQ private-chat conversation identity is the umo `Monika:FriendMessage:2441388327` (platform instance `Monika` = aiocqhttp). Its current conversation (`5d281073-b398-4c2e-aa70-ca3cfc6b445f`) holds 1,386,776 characters of history. Persona `Monika` (personas.id=1) carries the long system prompt that the memory plugins append to.
- `astrbot/core/conversation_mgr.py` keys every conversation, persona binding and memory-plugin record by unified_msg_origin; `astrbot/core/platform/astr_message_event.py:105-114` exposes `unified_msg_origin` as a property whose setter runs `self.new_session = MessageSession.from_str(value)`. A plugin that builds its own event can therefore point the umo at an existing QQ session.
- The bundled `webchat` adapter forces `abm.session_id = f"webchat!{username}!{cid}"` (`webchat_adapter.py:213`), so it cannot impersonate the QQ session.
- HAST-SEC 0.1.1 sits at `D:\HAST-SEC-v0.1.1\HAST-SEC-v0.1.1` (Node >= 22, `@pixiv/three-vrm` 3.5.5 + three 0.180.0, default port 8769, currently not running). It is a 0.8B Chinese *arm-motion* intent model behind `POST /v13-api/plan`, wrapped in a strict contract and a same-origin check.
- `素材/` (Poly Haven PBR sets, 8K HDRI, Styloo classroom GLB/FBX pack, `莫妮卡.vrm` 22.01 MB) is entirely untracked by git today; the repository tracks 97 files and none of the assets.

**Hard constraints**

- The VRM is VRM **0.0**, has **zero animation clips**, 562 nodes, 399 facial morph targets, and `firstPerson.lookAt` is absent (`hasLookAt: false`). Every motion, blink and gaze behaviour must be generated in code.
- HAST-SEC's language model only emits high-level arm intent JSON; trajectory decoding runs inside *its own* browser page over statically served model files under `models/`. It is not a headless animation service.
- HAST-SEC's `service/public_api.mjs` rejects any request whose `origin` header does not equal `http://` + its own `host` header, with `只接受本机页面请求.`.
- QQ must keep working exactly as it does now. The game is an additional client, never a replacement.

## Goals / Non-Goals

**Goals:**

- One conversation, one persona, one set of memory plugins for both QQ and the 3D classroom, achieved by sharing the umo rather than duplicating state.
- A dialogue loop that degrades in defined steps (main channel → local fallback lines → action-backend text) with no dead ends in the UI.
- Deterministic, testable mapping from a reply to a bounded set of expressive cues, independent of which model produced the reply.
- A motion layer with three explicit tiers, so HAST-SEC is an enhancement and never a load-bearing dependency.
- An asset pipeline that keeps multi-hundred-megabyte binaries out of git while remaining reproducible on this machine.

**Non-Goals:**

- No second brain: the game does not maintain its own persona, memory store or prompt assembly.
- No per-turn prompt surgery on the shared persona (see D6); the QQ output must stay byte-identical to today's.
- No trajectory-level reuse of HAST-SEC's motion models in v1 (D8); no bundling of its weights.
- No mobile layout, no VR, no multiplayer, no voice in v1 — as fixed by `proposal.md`.
- No history rewrite of the already-pushed repository to purge assets.

## Decisions

### D1 — Share the QQ session through a custom platform adapter with umo overwrite

The bridge is an AstrBot plugin (`astrbot_plugin_mas3d_game`) that registers a platform adapter, builds an `AstrMessageEvent` per game turn, and overwrites its `unified_msg_origin` with the configured QQ umo before dispatching it into the normal pipeline. Replies are captured at the adapter's send boundary and pushed to the browser instead of to QQ.

*Why:* persona, conversation history, the eight memory/companion plugins, tool calling and provider selection are all keyed by umo. Pointing the umo at the existing session reuses all of it with zero duplication, and the reply that lands in the classroom is literally the same reply QQ would have received.

*Alternatives rejected:*

- `webchat` adapter — its session id is force-prefixed with `webchat!`, so it always creates a separate conversation and a separate memory namespace.
- Standalone FastAPI bridge with its own umo plus memory injection/summarisation — would have to re-implement persona assembly, context trimming and every memory plugin's contract; guarantees drift from the QQ Monika.
- Read `data_v4.db` and call a provider directly — bypasses tool calling, persona folding and memory plugins; also duplicates credential handling.

*Cost:* the umo setter is an internal API; D-risks below cover the guard.

### D2 — The bridge lives inside the AstrBot process, not beside it

The plugin exposes its own HTTP/WebSocket routes via `context.register_web_api(` (`astrbot/core/star/context.py:705`), served by AstrBot's existing aiohttp/FastAPI stack.

*Why:* the shared conversation can only be driven in-process; provider credentials never leave AstrBot; no second port, process or supervisor to babysit on a Windows desktop install.

*Alternatives rejected:* a separate Python service calling the plugin over RPC adds a hop, a failure mode and a deployment step without removing any work, because the conversation manipulation has to happen in AstrBot anyway.

### D3 — WebSocket for dialogue, HTTP for everything else

`/mas3d/ws` carries turn/subscription traffic (client `say`, server `ack` → `thinking` → `reply` → `idle`, plus `intent` frames). `/mas3d/api/*` carries health, settings echo and the action-backend proxy.

*Why:* "回复等待状态" needs server push, a turn needs cancellation, and one long-lived socket makes reconnection and latency measurement trivial. Static asset proxying and the action call are single request/response and do not belong on the socket.

### D4 — Localhost binding with an explicit shared secret

The listener binds `127.0.0.1` only. On first start the plugin generates a 32-byte token into `plugin_data/mas3d/token`; the web client reads it from `VITE_MAS3D_TOKEN` at build/dev time and sends it as `Authorization: Bearer` (WS: query parameter or first frame). CORS is restricted to the Vite dev origins and the production static origin.

*Why:* without this, any page in the user's browser could drive the QQ bot and write into a 1.38 MB personal conversation. Origin checks alone are also trivially satisfied by a malicious localhost page.

### D5 — Game turns are injected with a visible scene marker

Each game turn is sent as `（3D教室）` + player text, and the marker is stripped again for display in the classroom.

*Why:* the LLM and the memory plugins need a cheap way to tell "she is in the classroom now" from a QQ message; a readable prefix also stays legible if it ever surfaces in QQ history, unlike zero-width or control characters, which would silently corrupt the shared transcript.

*Consequence to accept:* every game turn lands in the QQ conversation, as the user explicitly asked. Token cost is therefore real; D10 keeps the game from adding extra context of its own.

### D6 — Expression cues are derived locally and deterministically, not requested from the model

The reply text is normalised by a pure function in `packages/motion`: quotes and reported speech are stripped, then emotion words, punctuation intensity, sentence-final question form and action verbs are matched against a fixed table, producing `{emotion, strength, gaze, gesture}` within bounded enums.

*Why:* the persona prompt is shared with QQ, so asking the model for `<mood>` tags would either leak the tags into QQ replies or require per-platform prompt forks inside astrbot's persona folding — both worse than a 200-line deterministic mapper that can be unit-tested and tuned without burning model calls.

*Alternatives rejected:* a second LLM call to classify the reply (adds latency and cost per turn, and is non-deterministic); per-turn `system_prompt` injection through `llm_generate`/`tool_loop_agent` (bypasses the persona folding and memory plugins that D1 exists to reuse). A later change may add an optional tagging mode once astrbot gains a per-platform persona channel; the interface (`ExpressionCue`) is designed to accept it unchanged.

### D7 — Three motion tiers with an explicit arbitration rule

- **Tier 0 — generated idle:** breathing (chest/spine sine, ~4 s period), blink (Poisson-ish interval 2.5–6 s, 90 ms close), saccadic gaze with head-lead, micro-sway.
- **Tier 1 — procedural gestures:** nod, shake, tilt, wave, lean-in, look-away, authored as eased keyframe curves over the humanoid bones (no clips exist in the VRM).
- **Tier 2 — HAST-SEC arm intents:** `side`/`action`/`height` mapped onto upper-arm + forearm + hand rotation targets, played with `speed`, `strength`, `hold`, `delay`.

Arbitration: Tier 2 claims the arm bones while active; Tier 1 attempts the same bones, so a running Tier 2 intent suppresses new Tier 1 gestures but not Tier 0. Higher strength preempts lower; preemption blends out over 150 ms. Every layer has a maximum duration so the avatar cannot freeze mid-pose.

*Why:* HAST-SEC cannot blink, breathe or nod, and it needs a warm GPU-backed model for what it does cover. Making it the base would make the character lifeless whenever it is down.

### D8 — HAST-SEC is consumed at intent level, through a server-side proxy, with a persistent session id

The plugin proxies `POST /v13-api/plan` from Python, setting `Host` and `origin` to `http://127.0.0.1:8769` (the browser cannot forge either), `content-type: application/json`, a `session` GUID generated once per game session and persisted, `text` truncated to 300 characters, and `context` capped at 4 entries / 1200 characters / 600 per entry. `generation` is fixed to `v15`. Responses are validated against `motion_result.mjs` rules before use.

*Why intent level:* the trajectory decoder is browser code served from `models/`; reusing it means embedding a 1.1 GB runtime and HAST-SEC's own three@0.180 build, which locks the renderer to their version and their page. Consuming `{base, intents[]}` gets the arm behaviour the user wants with ~200 lines of mapping, and leaves a documented upgrade path.

*Why server-side:* the same-origin check rejects any browser direct call; a Vite dev proxy would work in development and fail in production, so one path serves both.

*Failure policy:* status probe with 1.5 s timeout at session start and after a failure; on failure the tier is disabled for 60 s (cooldown) then re-probed; `unsupported` operations are discarded silently into Tier 1.

### D9 — Defined degradation chain and a single UI state machine

`idle → sending → thinking → streaming → idle`, with `error` reachable from any state and a retry affordance. Reply source priority: main channel → local fallback pool (per emotion bucket, seeded) → action-backend `reply` text if present (marked "降级" in the UI) → hard error card with a retry button. Bridge unreachable for >3 s flips the console into offline mode with a banner while the scene stays interactive.

*Why:* the requirement is that the game keeps working when a model fails; a state machine plus a priority list makes every failure path testable rather than emergent.

### D10 — Asset pipeline keeps binaries out of git, and only the avatar is an exception

- `素材/` stays untracked (it is today); `.gitignore` gains explicit `/素材/` and `/apps/web/public/assets/` entries.
- `tools/sync-assets.mjs` reads a checked-in `assets.manifest.json` and writes derived files into `apps/web/public/assets/`: HDRI downsampled to 2K EXR + 1K preview JPG, PBR maps to 1K WebP, classroom GLBs trimmed to the props actually placed in the scene.
- `莫妮卡.vrm` (22.01 MB) is copied into `apps/web/public/assets/monika/` **and tracked**, because the scene is meaningless without it and 22 MB is well inside the hosting limit; every other asset is derived and regenerable.

*Why:* the classroom pack is ~820 MB and the HAST-SEC runtime ~1.1 GB; tracking either makes the clone unusable. One tracked blob keeps a fresh clone renderable.

### D11 — Monorepo layout, with a junction for the plugin

```
apps/web/                 # Vite + React + three renderer, dialogue console, motion player
apps/bridge/              # AstrBot plugin (python package), also the HTTP/WS server
packages/protocol/        # shared TS types + runtime validators for frames and intents
packages/motion/          # cue mapper, gesture curves, tier arbitration (DOM-free, unit-tested)
tools/                    # sync-assets.mjs, mock-bridge.mjs, link-plugin.ps1
assets.manifest.json      # declared asset derivatives
```

AstrBot only loads plugins from `~/.astrbot/data/plugins/`, so development uses a directory junction from `astrbot_plugin_mas3d_game` to `apps/bridge`; `tools/link-plugin.ps1` creates or refreshes it, and release packaging copies instead.

*Why:* one repository, one `openspec/`, no build-time copying during development, and the plugin stays loadable exactly as AstrBot expects.

### D12 — Scene assembly reuses the classroom pack, faces one wall into space

Styloo props are instanced for one seating bay: desk, chair, blackboard slab, ceiling light strip. The open side becomes a hull cut-out with the NightSky HDRI on a large inverted sphere, plus a dim directional key so Monika stays readable. Camera is constrained to a 90° arc around her seat with clamped orbit and no free flight.

*Why:* the asset pack is a full classroom with walls on every side; a space-station classroom needs one missing wall (the DDLC-ending read). Constraining the camera keeps the composition and hides the seams that a full pack would expose.

### D13 — Client state and persistence split

Zustand holds ephemeral runtime state (connection, turn phase, cue queue, tier availability). Settings (volume, gaze sensitivity, motion-tier toggle, camera sensitivity) persist in `localStorage`. Transcript is *not* the source of truth — the AstrBot database is — the client keeps the last 50 turns for reconnect rendering and offers an export of that window only.

*Why:* duplicating the transcript invites divergence from the 1.38 MB shared conversation; the classroom only needs enough to render on reconnect.

### D14 — Verification strategy per layer

- `packages/motion`: vitest unit tests for cue mapping (quote stripping, bound clamping, preemption ordering) and curve sampling.
- `packages/protocol`: validator round-trip and rejection tests.
- `apps/bridge`: pytest with a stubbed `Context` asserting umo overwrite, marker prefixing, timeout fallback, token rejection, and HAST-SEC header forging (a local echo server asserts the received `origin`/`host`).
- `apps/web`: Playwright smoke against `tools/mock-bridge.mjs` — scene mounts, a turn completes, expression changes, offline banner appears when the socket drops.
- Manual acceptance: VRM expression/gaze fidelity, HAST-SEC online vs. offline arms, QQ side-effect check (the turn appears in QQ history and the reply matches).

### D15 — Expressions are driven by a first-party face driver over morph influences, not by `VRMExpressionManager`

Measured facts about `素材/莫妮卡.vrm`: the VRM 0.0 `blendShapeMaster` declares 18 expression groups (Neutral, A, I, U, E, O, Blink, Blink_L, Blink_R, Angry, Fun, Joy, Sorrow, Surprised, LookUp/Down/Left/Right) but **every group's `binds` array is empty**, so `@pixiv/three-vrm`'s expression manager would write nothing. The face mesh nevertheless carries **57 morph targets whose names survive in `meshes[0].primitives[0].extras.targetNames`** — the full VRoid set `Fcl_ALL_*`, `Fcl_BRW_*`, `Fcl_EYE_*`, `Fcl_MTH_*`, `Fcl_HA_*`. The names were verified against decoded geometry: index 13/14/15 are `Fcl_EYE_Close`/`_R`/`_L` with vertex spans 0.156 vs 0.091/0.091, index 27 `Fcl_MTH_Down` moves mouth vertices by −0.013 in Y, indices 39–43 are `Fcl_MTH_A/I/U/E/O` all touching ~1400 mouth-region vertices, index 44 `Fcl_HA_Hide` retracts teeth 0.056 along +Z, and index 11 `Fcl_EYE_Natural` is an empty target.

So: a `tools/extract-face-map.mjs` pass emits a committed `face.map.json` (name → influence index plus the semantic preset table), and a small `FaceDriver` writes `mesh.morphTargetInfluences[index]` at load time, preferring names read from `gltf.parser.json` and falling back to the committed map. Preset expressions are composed from VRoid components (joy = `ALL_Joy` + `EYE_Joy` + `MTH_Joy`, surprised = `ALL_Surprised` + `EYE_Spread` + `MTH_Large`, blink = `EYE_Close` or the `_L`/`_R` pair for a wink) with a clamped sum so layered presets cannot overdrive the mesh.

*Why not patch the VRM's empty binds:* rewriting the container to add binds would fork a 22 MB binary we cannot regenerate; driving influences directly is ~60 lines and keeps the original asset byte-identical.

### D16 — Voice is synthesised by talking to GPT-SoVITS `api_v2` directly, not through AstrBot's TTS provider

The installed `gsv_tts-zh` provider works, but reusing the provider object loses the two things lip sync needs: **the exact text that was actually sent to the synthesiser** (the provider translates Chinese to English internally via `gsv_tts_translate.json`) and **per-sentence timing**. It also hides weight selection behind `initialize()`, while three local sources disagree about which checkpoints are live: `GPT_SoVITS/configs/tts_infer.yaml` says `Moniar_v2_1-e15` + `Moniar_v2_1_e12_s984.pth`, `weight.json` says `Moniar_e8_s504.pth`, and `api-start.bat` documents `Moniar_v2-e15` + `Moniar_v2_e16_s368`.

So the bridge owns a `TtsClient`: call `/set_gpt_weights` and `/set_sovits_weights` explicitly, then POST `/tts` with the values measured as good (`top_k=10`, `top_p=1`, `temperature=1`, `text_split_method=cut5`, `repetition_penalty=1.35`, `speed_factor=1.0`, `streaming_mode=false`, `media_type=wav`, the same `ref_audio_path`/`aux_ref_audio_paths`/`prompt_text` the provider uses). Measured: 2.37 s to synthesise 4.40 s of 32 kHz mono 16-bit audio when warm. Configuration reuse is by *value*, not by object: the translate settings are read from `~/.astrbot/data/gsv_tts_translate.json` and the checkpoint paths are pinned in this project's config, so the classroom and QQ sound like the same person without sharing a code path.

Synthesis is single-concurrency with a depth-2 queue, a 30 s per-sentence and 60 s per-turn timeout, and results cached at `<plugin_data>/mas3d/cache/tts/<sha256(text|weights|params)>.wav` (LRU 500). *Why:* QQ and the classroom share one GPU; a burst of parallel requests would starve the bot's own replies.

### D17 — Subtitles are Chinese, audio is English

The voice was trained on English-only data (documented at the top of `gsv_selfhosted_source.py`): Chinese phonemes are outside its distribution and produce slurred syllables. So the pipeline sends the reply through the same GLM-based translator the QQ bot uses (`enable: true`, `model: glm-5.3-flash`, `temperature: 0.3`), synthesises the English, and displays the Chinese. If translation fails, the raw Chinese is synthesised anyway — degraded but audible — and if synthesis fails, the turn is subtitles-only. The player never sees English text, so a paraphrase-level translation loss only affects the mouth shapes, not comprehension.

### D18 — Lip sync is three layers with the audio envelope as the mandatory one

- **L0 envelope (mandatory, zero dependencies):** a WebAudio `AnalyserNode` (or a precomputed RMS curve from the served WAV) drives `Fcl_MTH_A` through a gate plus asymmetric smoothing, with a small fraction bled into `Fcl_MTH_Down` so open shapes read as jaw movement rather than a widening stretch.
- **L1 sentence timing (preferred):** the reply is split on punctuation, synthesised sentence by sentence, and the accumulated WAV durations become the schedule; the mouth closes to zero in the gaps and at the end, which is what stops the "continuous humming mouth" look.
- **L2 viseme selection (enhancement):** an English grapheme→viseme rule table (a/ɑ→A, i/ɪ/y→I, u/ʊ/w→U, e/ɛ→E, o/ɔ→O, bilabials and pauses→0.15 baseline) biases which of the five vowel morphs leads while the envelope still supplies the amplitude. No forced alignment model is introduced — this is the layer that must degrade silently if the text/audio disagree.

*Why envelope-first:* phoneme alignment needs a model we do not have, and an incorrect viseme sequence is far more uncanny than a correct amplitude with approximate vowels.

### D19 — The mouth channel has an owner

Speech, the Tier 0 idle, and the expression presets all want `Fcl_MTH_*`. Rule: while audio is playing, the face driver owns the mouth channels and expression contributions to `MTH_*` are multiplied down to 0.25; on speech end the expression weights ramp back over 200 ms. Eyes, brows, and head gestures are untouched, so "smiling while talking" still reads as smiling. This is an extension of the D7 arbitration rule and is unit-tested with the same harness. Voice never touches AstrBot's `provider_tts_settings` (currently `enable: false`), so the QQ channel stays text-only.

Voice is **on by default** (user decision): the first reply in a fresh session is already audible, and the "subtitles only" switch in settings is the single escape hatch — it stops synthesis entirely rather than muting playback, so no requests are made while it is off.

### D20 — The whole reply is captured, and the platform send path is diverted

Because the game turn shares the QQ umo (D1), every plugin bound to that umo also processes it. Acceptance exposed the consequence: `astrbot_plugin_splitter` runs last (`priority = -100000000000000000`) and rewrote `result.chain` into the fifth of five segments, posting each segment itself through `context.send_message(event.unified_msg_origin, …)`. The WS therefore only ever received the last sentence while the database held the full reply. The rules that came out of it:

- The turn text is stored from `on_llm_response` (`response.completion_text`) and again from `on_decorating_result` (`result.get_plain_text()`), both into an event extra; the send-boundary chunk collection is only the fallback.
- For game events the result is marked with the splitter own idempotence flag (`__splitter_processed`), so it neither re-segments nor posts to QQ.
- For game events only, `ResultContentType.LLM_RESULT` is downgraded to `GENERAL_RESULT`: text is still delivered, but `result_decorate` skips its TTS branch. That TTS is keyed on the shared umo through `SessionServiceManager`, cannot be disabled per event, cost ~80 s per turn, and failed with HTTP 400 whenever the translated line contained an emoji.
- Cross-origin access from the page to the bridge is required for voice, so CORS is opened for `http://localhost|127.0.0.1(:port)` only; the token stays in the `Authorization` header and credentials are not allowed.

*Why not turn the splitter off in its own config:* that is machine state, not repository state, and forking a third-party plugin breaks on its next release. Reusing a flag it already treats as idempotence costs nothing and degrades to the earlier behaviour instead of failing.

Acceptance compares the WS text with the conversation row verbatim (D14) precisely because a plausible-looking reply hid this for a full round.

### D21 — A turn has an identity, and the wire is allowed to be slower than the mock

An independent review of the finished branch found the failure mode that mock testing was structurally blind to: the client gave up after 8 s while the server allowed 120 s, and `ack`/`thinking` did not re-arm the timer, so **every real turn (measured 27.7 s) would have been declared a timeout** and answered with a local fallback line before the real reply arrived. The same review found that replies carried no correlation, so a late frame from turn N could be rendered as the answer to turn N+1.

The rules:

- The client sends its own `turnId`; the server echoes it on every frame of that turn. `acceptsReply(pending, turnId)` drops any reply that does not match the turn being waited on, so a slow turn can never answer a newer question.
- The client wait budget (150 s) is strictly larger than the server's turn timeout (120 s). The server is the only place allowed to declare a turn dead, otherwise both sides race.
- `ack` and `thinking` re-arm the wait timer: the budget measures silence, not elapsed time.
- Only `bridge_down` produces the local fallback line. A timeout or a model error leaves the transcript alone — she may already have answered, and inventing “I did not hear you” next to a real reply is worse than saying nothing.
- A closed socket ends the pending turn immediately instead of waiting out 150 s, and reconnects with exponential backoff (1 s → 8 s). The banner's button calls `reconnect()` rather than reloading the page, so a plugin reload (TECH.md's documented upgrade risk) no longer loses the scene.
- The whole conversation is serialised across connections, not just within one: every tab writes to the same QQ history.

*Why the mock could not catch it:* the scripted bridge answers in 760 ms, so a timeout of 8 s or 800 s is indistinguishable there. The budget is now asserted against the real chain time recorded in verification.md, and `VITE_MAS3D_REPLY_TIMEOUT_MS` exists only to make it smaller for mock runs.

Two smaller divorces fixed in the same round: the frontend prefix stripper now loops to a bound like the Python one, and `validatePlan` (TypeScript) and `validate_plan` (Python) are pinned to a single shared fixture (`packages/protocol/test/fixtures/plan-cases.json`) so the two hand-written validators cannot drift apart — `reply: null` in particular means *absent* on both sides.
## Risks / Trade-offs

- **Every game turn pollutes the QQ conversation** (user-requested, but it grows a 1.38 MB history) → marker prefix for readability, no extra context injected by the game, and a per-session turn counter surfaced in settings so the cost is visible; if it becomes a nuisance, the marker plus umo are both configuration values, so a dedicated classroom umo is a one-line change.
- **umo overwrite relies on an internal setter** (`astr_message_event.py:112`) → single adapter point, plus a startup self-check that probes the property setter and, if absent, falls back to an isolated umo with a visible warning rather than crashing the bot.
- **AstrBot upgrade can break the plugin** (4.28.2 internals) → pinned expectations documented in the plugin README, self-check on load, and disabling the plugin restores stock QQ behaviour with no residue.
- **HAST-SEC needs a warm 0.8B model** (first `plan` can take tens of seconds) → asynchronous `warm` on session start, a 1.5 s probe timeout, tier 2 disabled until warm, and a per-turn call timeout so dialogue never waits on it.
- **Forging `Host`/`origin` may be normalised by the HTTP client** → the pytest echo server asserts the exact headers the proxy sends before tier 2 ships; if the client rewrites them, the fallback is a minimal raw-socket POST.
- **VRM cost**: 562 nodes, 399 morph targets, many spring-bone hair chains → only the ~20 morphs actually used are driven, spring bones update at half rate, and the tier-2 path is capped to 4 intents per plan, exactly as the contract allows.
- **No authored clips means stiff motion** → all Tier 0/1 curves are eased with overlap (head leads gaze by one frame, breathing offsets chest/spine phase); acceptance is visual, so the tuning numbers live in one constants module.
- **Licence envelopes**: the VRM requires visible credit to `tyler1234506` and forbids commercial use; HAST-SEC is `hast-sec-noncommercial-0.1`; the Styloo pack must be re-checked before any redistribution → an attribution footer is a first-class UI element, and no HAST-SEC weights or third-party art are committed except the single VRM covered by that footer.
- **Windows-only assumptions** (junction, playwright paths) → the plugin and web app themselves stay platform-neutral; only `tools/link-plugin.ps1` is Windows-specific.
- **The voice checkpoints are configured in three disagreeing places** (`tts_infer.yaml`, `weight.json`, `api-start.bat`) → the bridge sets weights explicitly before every synthesis and surfaces the effective filenames in the voice settings panel, so a silent swap is visible instead of audible.
- **GPT-SoVITS and the QQ bot compete for one GPU** → single-concurrency queue, 30 s/60 s timeouts, cache hits served from disk, and a "subtitles only" switch that stops all synthesis.
- **Chinese→English translation can drift in tone** (the translator is tuned for a streamer persona) → the visible text is always the original Chinese reply; translation only affects audio and mouth shapes, and a translation failure still produces audio.
- **A wrong viseme map is more uncanny than none** → L2 is bounded by the L0 envelope, mappings live in one constants module with unit tests over a word list, and disabling L2 leaves a working amplitude-driven mouth.
- **Two validators, one contract** (`validatePlan` in TypeScript, `validate_plan` in Python) → both are driven by one shared fixture with 13 agree/disagree cases, so a rule change on one side fails the other side's suite.
- **Platform plugins act on game turns too** (the shared umo is the point of D1) → the whole reply is captured before the send path, the result is marked so the segmenting plugin skips, and acceptance compares the WS text with the database row verbatim; a future plugin with a later priority can still mutate the chain, and that comparison is what catches it.
- **The empty VRM expression binds tempt a runtime patch** → the face driver reads `primitive.extras.targetNames` and falls back to the committed `face.map.json`; the 22 MB avatar is never rewritten, so it stays byte-identical to the source and re-importable.

## Migration Plan

1. **P0 skeleton** — monorepo, protocol package, mock bridge, scene mounts with the VRM at rest, attribution footer.
2. **P1 dialogue loop** — plugin loads in AstrBot, umo overwrite verified against the real QQ session, WS turn completes end-to-end, settings and offline banner.
3. **P2 expression** — cue mapper, Tier 0 idle, Tier 1 gestures, preemption, unit + smoke tests.
4. **P3 action backend** — proxy with header forging, tier 2 mapping, warm/cooldown, degradation chain coverage.
5. **P4 voice** — face map extraction and the face driver, the TTS client with explicit weights, sentence pipeline and cache, envelope lip sync, mouth-channel arbitration, subtitles-only switch.
6. **P5 polish** — asset downsizing, camera constraints, lighting, transcript export, acceptance pass.

Rollback: disable the plugin in the AstrBot panel (QQ returns to stock behaviour immediately, no data migration), and the web app independently falls back to `mock-bridge` via `VITE_MAS3D_BRIDGE=mock`. No database migration is performed by this change; the only writes are ordinary conversation messages in the existing schema.

## Open Questions

- Should game turns be suppressed from echoing back to the QQ client (e.g. via the aiocqhttp echo-guard plugin already installed)? Deferrable: the marker prefix makes the current behaviour legible either way.
- Which one of the 37 configured providers should the classroom prefer when the persona allows several? Deferrable: the shared umo inherits whatever QQ uses today, which is the desired default.
- Is 8K HDRI detail needed behind the hull, or is the derived 2K enough? Deferrable to the visual pass; `assets.manifest.json` makes it a one-line change.
- Would a Chinese-trained checkpoint be worth it later, so the classroom can skip the translate step? Out of scope here (training is excluded), but the English hop is isolated behind one interface, so a Chinese checkpoint is a config swap.
- Should the classroom voice also be offered to QQ (AstrBot's `provider_tts_settings`), given both already share a session? Deferrable: leaving it off guarantees the game never changes QQ behaviour.