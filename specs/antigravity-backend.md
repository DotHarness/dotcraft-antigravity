# Antigravity backend

| Field | Value |
|---|---|
| Version | 0.4.1 |
| Status | Living |
| Date | 2026-11-25 |

## Ownership

The `antigravity` plugin contributes the `antigravity` model backend through DotCraft's model backend extension point and a Desktop module that surfaces limit and verification notices above the Composer. The official local Antigravity CLI (`agy`) owns model transport, Google/Enterprise authentication and credential storage. DotCraft owns instructions, conversation state, tool declarations, approval and execution.

The plugin never embeds OAuth client credentials, never reads or parses stored OAuth token files or OS keyring secrets, and never calls undocumented Google Cloud Code Assist endpoints directly.

## Workspace, agent and permission isolation

Each conversation run uses a private directory under the plugin's data root (`PrivateDirectory`, current-user ACL on Windows and `0700` on POSIX), outside any user repository, with:

- An isolated `HOME` / `USERPROFILE` directory containing `.gemini/antigravity-cli/settings.json` configured with `agentMode: ""`, `toolPermission: "request-review"`, `enableTerminalSandbox: false`, `disableSlashCommands: true`, `useG1Credits` set from `allowAiCredits`, `permissions.allow: ["mcp(dotcraft/*)"]`, and `permissions.deny: ["command(*)", "write_file(*)", "read_url(*)", "execute_url(*)"]`.
- Credential bridging without reading secret contents: on systems with file-based CLI tokens (`jetski-standalone-oauth-token` or `antigravity-oauth-token`), the token file is linked (and re-synced before resumed turns, retrying once on pre-output token refresh contention) into the isolated home; on macOS, `~/Library/Keychains` is symlinked unless `ANTIGRAVITY_CONFIG_DIR` is explicitly set; on Windows and Linux, Credential Manager and D-Bus Secret Service access are preserved (stripping SSH session env vars on Linux when no token file exists so `agy` uses keyring auth).
- A scrubbed child environment that removes unrelated secret-looking variables (`*_API_KEY`, `*_TOKEN`, `*_SECRET`, database/webhook URLs, etc.) while keeping runtime essentials, proxy settings (merging `localhost,127.0.0.1,::1` into `NO_PROXY` whenever a proxy or `ANTIGRAVITY_PROXY` is set), TLS cert paths and `AGY_CLI_DISABLE_AUTO_UPDATE=true`.
- A relay custom agent written to `<work>/.agents/agents/dotcraft-relay/agent.md` with `excludeDefaultComponents: true`, `tools: []` and DotCraft's full system instructions (plus tool-name mapping, required tool-choice guidance and JSON response-format guidance) as its Markdown body. Tool-enabled runs inherit the plugin-owned Steer hook from the isolated home. They do not load customizations from the user's real home or workspace.
- A workspace MCP configuration written to `<work>/.agents/mcp_config.json` registering only the run's loopback Streamable HTTP `dotcraft` MCP server (`serverUrl`, `url`, per-run Bearer token and `timeoutSeconds: 3600`).

## Sampling and tool continuation

1. `agy` launches in persistent streaming mode: `--input-format stream-json --output-format stream-json --disable-slash-commands --agent dotcraft-relay --model <resolved-slug>` (adding `--effort <token>` only for models that accept effort variants, and `--conversation <id>` when resuming a shelved session).
2. A cold run receives prior conversation as role-tagged JSON followed by all trailing user messages as the current user request on `stdin` (`{"event":"user","message":{"content":"..."}}`). Subsequent turns on the same live or resumed run send only the new user input so `agy` preserves its conversation and prompt cache.
3. Because `agy --input-format stream-json` accepts only `text` blocks on `stdin`, image/audio/video `DataContent` attachments are materialized into the run's private media folder, granted exact `read_file(<path>)` and `view_file(<path>)` rules in the isolated `settings.json` (respawning with `--conversation <id>` if an existing live worker predated the rule), and referenced in the user message as `[Image](<path>)`.
4. Stream events (`init`, `step_update`, `result`) project to provider-neutral `ChatResponseUpdate` items. Total input tokens combine uncached `input_tokens` with `cache_read_tokens` to match DotCraft's usage contract. Per-step `usage` from completed non-checkpoint `step_update` events is emitted immediately (including mid-turn before tool calls). When a turn produces no step-level usage, cumulative session usage from `result` is converted to per-turn deltas against the worker baseline. A `result` with `status == "SUCCESS"` that produced neither text nor prior tool activity fails the turn instead of committing an empty response.
5. When the model calls one or more DotCraft tools through the loopback MCP server, the server mints call IDs that remain unique across worker restarts, holds the HTTP `tools/call` requests open on pending completions, and coalesces concurrent calls arriving within a short batch window into one `ChatFinishReason.ToolCalls` boundary. DotCraft approves and executes the tools. The next sampling request supplies matching `FunctionResultContent` entries, including any images in those results. The plugin completes the pending MCP calls with typed results so the same live `agy` process continues. When matching tool results are followed by text-only conversation input, such as queued guidance or runtime reminders, agy's `PreInvocation` hook inserts it once, in order, as user messages before the next model call. The tool results remain unchanged and the native session continues without a new CLI prompt. The plugin fails the sampling request if the hook does not take the pending input. Unmatched results or unsupported input shapes still fall back to complete history replay.
6. Fail-closed guard: any `step_update` with `step_type == "tool"` targeting a tool outside the bridged `dotcraft` MCP catalog (including `call_mcp_tool` invocations whose `ServerName` is `dotcraft` and `ToolName` is declared) and authorized media-read paths immediately terminates the child process and fails the turn with `Antigravity.NativeToolBlocked`.

The Antigravity backend's chat client advertises `ModelSteeringPolicy` with `ModelSteeringMode.AtNextBoundary`. When the user requests immediate steering, DotCraft delivers the input at the ordinary steering boundary instead of canceling the native session. An explicit Stop still cancels the run.

Generation speed and first-token latency use Antigravity's own timing. The plugin reads generation metadata only from the run's isolated CLI session and reports timing only when it matches the usage for the same generation. A generation may include both a reply and tool calls. Its timing belongs with that generation's usage before the tool continuation, not with the next generation. Missing, ambiguous, or unreadable metadata does not affect the turn or its token counts. The host does not infer timing from CLI output arrival because `agy` can batch generated output.

Retained runs are scoped to provider, thread, context window and request kind. Continuation requires matching instructions, model, effort, tools, `allowAiCredits` and history prefix. Any changed request or history starts a fresh run from the complete DotCraft conversation. Compaction and non-turn requests end their run at the final result, and requests without conversation identity use isolated runs.

## Saved sessions

Idle conversation runs remain live for `idleTimeoutSeconds` (default 900 seconds, up to 6 concurrent idle runs). On idle expiry, overflow or orderly host shutdown, a run that completed at a non-tool boundary is shelved. The worker and MCP endpoint stop, but the private CLI conversation is retained for up to seven days (at most 64 shelved sessions per workspace). Shelved sessions retain neither bridged credentials nor endpoint tokens.

The next matching turn in the same workspace, provider, thread and context window resumes with `--conversation <id>` and sends only the new user input. This works across host restarts without relying on the host's process-local backend generation. The plugin checks both the sampling configuration and the full covered history prefix before resuming. A missing, changed or rejected session falls back to a fresh run with complete DotCraft history. Unfinished turns and unanswered tool calls are not saved. Authentication resets invalidate the workspace's saved sessions.

Thread forks and subagents receive their DotCraft-provided history in independent CLI conversations. Antigravity's interactive `/fork` does not provide a headless interface for branching an identified source at the required boundary. The plugin never resumes a parent conversation on behalf of a child.

## Account, model discovery and runtime metadata

- One `cli` authentication method (`Antigravity CLI account`) delegates credentials to `agy`.
- Executable lookup honors `executablePath`, `ANTIGRAVITY_COMMAND` / `AGY_CLI_PATH` / `ANTIGRAVITY_CLI_PATH` and `PATH` before probing standard per-OS install locations, including `%LOCALAPPDATA%\agy\bin\agy.exe` on Windows.
- The authentication status check verifies CLI presence (`Antigravity.CliNotFound`) and probes authentication via `agy models` (`Antigravity.LoginRequired` when unauthenticated), without ever reading credential contents. Authenticated status is cached with single-flight execution for 60 seconds and invalidated on login, logout or unauthorized sampling failures.
- Sign-in probes fresh CLI status and completes immediately when already signed in. Otherwise, it launches `agy acp --login` with `BROWSER=dotcraft-no-browser`, extracts the sign-in link from either `stdout` or `stderr` as an `auth_url` event, and races `manual_code` prompts against CLI exit so both copy-paste codes and browser localhost callbacks complete cleanly.
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
