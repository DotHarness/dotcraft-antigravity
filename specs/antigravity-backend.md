# Antigravity backend

| Field | Value |
|---|---|
| Version | 0.9.0 |
| Status | Living |
| Date | 2026-11-27 |

## Ownership

The `antigravity` plugin contributes the `antigravity` model backend through DotCraft's model backend extension point and a Desktop module that surfaces limit and verification notices above the Composer. The official local Antigravity CLI (`agy`) owns model transport, Google/Enterprise authentication and credential storage. DotCraft owns instructions, conversation state, tool declarations, approval and execution.

The plugin never embeds OAuth client credentials, never reads or parses stored OAuth token files or OS keyring secrets, and never calls undocumented Google Cloud Code Assist endpoints directly.

## Workspace, agent and permission isolation

Each conversation run uses a private directory under the plugin's data root (`PrivateDirectory`, current-user ACL on Windows and `0700` on POSIX), outside any user repository, with:

- An isolated `HOME` / `USERPROFILE` directory containing `.gemini/antigravity-cli/settings.json` configured with `agentMode: ""`, `toolPermission: "request-review"`, `enableTerminalSandbox: false`, `disableSlashCommands: true`, `useG1Credits` set from `allowAiCredits`, `permissions.allow: ["mcp(dotcraft/*)"]`, and `permissions.deny: ["command(*)", "write_file(*)", "read_url(*)", "execute_url(*)"]`.
- Credential bridging without reading secret contents: on systems with file-based CLI tokens (`jetski-standalone-oauth-token` or `antigravity-oauth-token`), the token file is linked (and re-synced before resumed turns, retrying once on pre-output token refresh contention) into the isolated home; on macOS, `~/Library/Keychains` is symlinked unless `ANTIGRAVITY_CONFIG_DIR` is explicitly set; on Windows and Linux, Credential Manager and D-Bus Secret Service access are preserved (stripping SSH session env vars on Linux when no token file exists so `agy` uses keyring auth).
- A scrubbed child environment that removes unrelated secret-looking variables (`*_API_KEY`, `*_TOKEN`, `*_SECRET`, database/webhook URLs, etc.) while keeping runtime essentials, proxy settings (merging `localhost,127.0.0.1,::1` into `NO_PROXY` whenever a proxy or `ANTIGRAVITY_PROXY` is set), TLS cert paths and `AGY_CLI_DISABLE_AUTO_UPDATE=true`.
- A workspace rules file (`AGENTS.md`) in the private working directory carrying DotCraft's full system instructions, tool-name mapping, required tool-choice guidance, JSON response-format guidance and relay isolation rules. Runs execute on the CLI's default agent without `--agent` or `.agents/agents/*` definitions so conversations remain on standard quota billing without leaking customizations from the user's real home or workspace.
- Only the run's authenticated loopback Streamable HTTP `dotcraft` MCP server is registered in the ACP session. The plugin supplies its URL and per-run Bearer header to both `session/new` and `session/resume`. ACP resume does not restore the relay from the working directory's MCP configuration.

## Sampling and tool continuation

1. Sampling uses JSON-RPC over `agy acp --process-per-session`. The run selects its model and reasoning effort using ACP session configuration. A model or effort change between completed turns keeps the native session. A failed configuration request does not send the user prompt or discard a resumable conversation.
2. A cold session receives the current model-visible history from DotCraft, not the raw rollout. The first ACP prompt places role-tagged history before the current user input. Short history stays inline. The most recent complete message groups within the inline budget stay together, including each tool round. A line-indexed private file holds omitted groups for on-demand reading. Historical tool calls and results are evidence, not executable tool requests.
3. Attachments that use private file references are granted exact read access for that run. The plugin does not authorize other local files or native tools.
4. ACP updates and prompt results project to provider-neutral text, reasoning, usage and finish reasons. Context occupancy uses the latest `usage_update.used` for the current session, independently of billing. ACP does not report the request's input count with that measurement. The host displays the measurement and uses model-visible history to decide when to compact. Without a window update the host estimates occupancy from history, not accumulated ACP billing. Billing uses the prompt result and includes cached input. At tool boundaries the plugin can report completed native generations early. It deducts those counts from the prompt result so each generation is counted once, including across a Steer handover. Generation speed uses output tokens and their matching native streaming duration, not tool time, first-output wait or output arrival times. When no matching timing is available, the plugin reports only usage.
5. When the model calls DotCraft tools, the MCP server holds the requests open, coalesces concurrent calls into a tool boundary, and returns their calls to DotCraft for approval and execution. The next sampling in the same turn returns matching results, including text, images and errors. The plugin grants ACP dispatch permission only for the declared loopback relay tools and explicitly authorized private file reads. Other permission requests are rejected. When a rejected built-in tool ends the ACP prompt without further progress, the plugin sends one corrective follow-up prompt on the same session so the model can continue with DotCraft tools, failing closed only if an unauthorized tool executes or repeats after recovery.
6. The backend advertises `ModelSteeringMode.AtNextBoundary`, so DotCraft delivers guidance without interrupting the active provider stream. If the ACP turn is waiting for tools, guidance is sent as an additional native prompt and the original tool results follow on MCP. Steer does not send `session/cancel`. At a completed answer boundary, new input is sent as the next prompt on the same session.

Retained runs are scoped to provider, thread, context window and request kind. Live reuse requires matching instructions, tool declarations, `allowAiCredits` and history prefix. Compaction and non-turn requests end their run at the final result, and requests without conversation identity use isolated runs.

The request timeout bounds ACP setup and the wait for the first model output or tool activity of each sampling round. A user input echo does not count as model progress. Once progress arrives, generation can continue until it completes or the user stops it.

## Saved sessions

Idle conversation runs remain live for `idleTimeoutSeconds` (default 900 seconds, up to 6 concurrent idle runs). Idle expiry, overflow or orderly host shutdown can shelve a run at a completed or confirmed interrupted boundary. A prompt still waiting for tools is active, not idle. Shelving stops the worker and MCP endpoint but retains the private CLI conversation for up to seven days (at most 64 shelved sessions per workspace) without bridged credentials or endpoint tokens.

The next matching turn in the same workspace, provider, thread and context window reopens the saved native ID using `session/resume`. This also opens a retained headless conversation in its original isolated directory without copying its database or replaying covered history. Native resume requires matching instructions, tool declarations, `allowAiCredits` and the full covered history prefix.

Stop sends `session/cancel` for the active ACP prompt. After the prompt acknowledges cancellation, the plugin removes pending relay calls and retains the worker and native session. Stop also cancels an ACP prompt that is parked at a DotCraft tool boundary. A new DotCraft turn never continues the old turn's pending MCP calls. If the CLI fails to confirm within a bounded interval, the plugin force-stops the worker and retains the native directory and accepted prompt checkpoint for the next resume. The next prompt sends the history after the accepted prompt and the new input. Assistant output and recorded tool results from the interrupted turn are evidence, not active tool calls.
A missing, incompatible or rejected session falls back to a cold run. Private handoff evidence remains readable after native resume and is deleted with the run directory. Authentication resets invalidate the workspace's saved sessions.

Thread forks and subagents receive their DotCraft-provided history in independent CLI conversations. Antigravity's interactive `/fork` does not provide a headless interface for branching an identified source at the required boundary. The plugin never resumes a parent conversation on behalf of a child.

## Account, model discovery and runtime metadata

- One `cli` authentication method (`Antigravity CLI account`) delegates credentials to `agy`.
- Executable lookup honors `executablePath`, `ANTIGRAVITY_COMMAND` / `AGY_CLI_PATH` / `ANTIGRAVITY_CLI_PATH` and `PATH` before probing standard per-OS install locations, including `%LOCALAPPDATA%\agy\bin\agy.exe` on Windows.
- The authentication status check verifies CLI presence (`Antigravity.CliNotFound`) and probes via `agy models` without reading credential contents. Only an explicit CLI authentication error from the probe reports `Antigravity.LoginRequired`. Timeouts and other failures report that status could not be checked. A failed probe leaves existing workers and saved sessions intact. Authenticated status is cached with single-flight execution for 60 seconds and invalidated on login, logout or unauthorized sampling failures.
- Sign-in probes fresh CLI status and completes immediately when already signed in. If the CLI reports that sign-in is required, the plugin launches `agy acp --login` with `BROWSER=dotcraft-no-browser`, extracts the sign-in link from either `stdout` or `stderr` as an `auth_url` event, and races `manual_code` prompts against CLI exit so both copy-paste codes and browser localhost callbacks complete cleanly. A completed sign-in invalidates the previous account's sessions.
- Sign-out stops retained runs, clears cached auth/catalog/usage/limit state, and invokes `agy -p "/logout" --output-format json`.
- Model discovery parses `agy models`, grouping effort-suffixed slugs (`-low`, `-medium`, `-high`, `-xhigh`, `-max`) into one public model entry with `ModelReasoningSupport` while preserving exact CLI slugs per effort. `IProviderModelRuntimeResolver` projects model metadata, vision capability and context window bounds matching `agy` (`1_048_576` for Gemini/Orchestrator, `1_000_000` for Claude, `131_072` for GPT-OSS).

## Quota, early failure detection and usage

- Usage queries run `agy -p "/usage" --output-format json` in an isolated workspace with `disableSlashCommands: false`, single-flight execution, a 60-second fresh TTL, and a shared disk cache (`~/.craft/cache/antigravity-usage.json`) with 30-minute stale-while-revalidate so workspace switches return immediately while refreshing in the background, consuming zero inference tokens.
- Returned quota groups and buckets map to `ProviderUsageSnapshot` (`Primary` for the tightest 5-hour pool and `Secondary` for the tightest weekly pool when both exist, falling back to `Primary` for the tightest pool overall—preferring Gemini pools when present so other model groups do not skew the summary—along with `Values["pool.<key>.remaining"]`, `Values["pool.<key>.reset_epoch_ms"]`, `Labels["pool.<key>.name"]`, `Labels["pool_order"]`, and optional `PlanType` and `Credits`).
- Error messages from `result.error`, `stderr` and an isolated `cli-*.log` early-quota watchdog classify `RESOURCE_EXHAUSTED` / `Individual quota reached` (`quota_reached`), `AI credits balance is too low` (`credits_exhausted`) and `VALIDATION_REQUIRED` (`validation_required` with extracted `https://accounts.google.com/...` verification link), updating account limit state to avoid retry loops and surfacing banners in Desktop.

## Desktop module

The Desktop module adds `composer.before` exclusively for providers with `backendId === "antigravity"` to show quota-reached, AI-credits-exhausted and Google account verification banners using `provider/auth/usage`, while delegating the Composer usage pill, hover tooltip and detail popover to DotCraft's built-in provider usage UI.

## Run lifetime

Cancellation, abandoned streams and account changes release child process trees, loopback MCP listeners, pending tool calls and unsaved private run directories. Startup reaps recorded orphan workers before removing unsaved directories. It leaves directories owned by another active backend untouched.
