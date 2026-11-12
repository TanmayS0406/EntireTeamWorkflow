# Entire Team Workflow

A Windows desktop app (Blazor) backed by Windows services that runs a **multi-agent software team** on top of Claude:
you load user stories, and the agents build the application in a folder you choose — backend and UI engineers working
**in parallel**, like a real team. Sign-in is with your own Claude account.

```
┌────────────────────────┐        REST + SignalR (127.0.0.1:5180, API key)
│ Etw.Desktop            │ ─────────────────────────────────────┐
│ WPF + Blazor WebView   │                                      ▼
│  · Claude sign-in      │                       ┌──────────────────────────────┐
│  · OAuth (Entra) login │                       │ EtwBackend  (Windows service) │  SQLite, SignalR hub,
│  · folder picker       │                       │ gateway + reverse proxy       │  projects / stories / runs
└────────────────────────┘                       └───────┬───────────┬──────────┘
                                                         │           │ proxies /api/integrations, /api/azure
                                   starts runs           ▼           ▼
                       ┌────────────────────────┐  ┌───────────────┐ ┌─────────────┐
                       │ EtwOrchestrator :5182  │  │ EtwIntegrations│ │ EtwAzure    │
                       │ Agent Framework+Claude │─▶│ :5181          │ │ :5183       │
                       └────────────────────────┘  │ Azure DevOps,  │ │ ARM, App    │
                          notifies / deploys ─────▶│ Slack, Teams   │ │ Service     │
                                                   └───────────────┘ └─────────────┘
```

| Project | What it is |
| --- | --- |
| `Etw.Desktop` | WPF shell hosting the Blazor UI in a `BlazorWebView` (folder picker, links) |
| `Etw.Desktop.UI` | The Blazor components: login, workflow canvas, backlog, runs, connections, settings |
| `Etw.Backend` | **Windows service.** REST API + SignalR + SQLite; reverse-proxies the other services (YARP) |
| `Etw.Orchestrator` | **Windows service.** Runs the agent team with Microsoft Agent Framework; Claude is the model |
| `Etw.Integrations` | **Windows service.** Azure DevOps (OAuth) work items, Slack and Teams notifications |
| `Etw.Azure` | **Windows service.** Azure sign-in (OAuth), subscriptions/resource groups, App Service deployment |
| `Etw.Shared`, `Etw.Hosting` | DTOs, Claude CLI wrapper, Entra OAuth (PKCE) · service plumbing (API key, logging, DPAPI secrets) |

## Requirements

- Windows 10/11 or Server 2019+, **.NET 10 SDK** (to build) — the installed app needs the .NET 10 Desktop + ASP.NET Core runtimes, or publish self-contained.
- **Claude Code CLI** (`claude`), installed and signed in or signable-in (https://claude.com/claude-code), plus **Git for Windows** (Claude Code's shell tool needs it).
- WebView2 runtime (present on current Windows).
- For the generated apps: the toolchains of the languages you pick (`dotnet`, `node`/`npm`, `python`, `java`/`mvn`, `go`, …). Agents build and test with them.

## Quick start (development)

```powershell
.\scripts\Start-Dev.ps1 -Desktop        # builds, starts the 4 services in console windows, opens the app
```

Use `-Hidden` to run the services without windows and `-DataDir <folder>` to keep a scratch database/secrets folder.
`.\scripts\Start-Dev.ps1 -Stop` stops them.

## Install as Windows services

From an **elevated** PowerShell:

```powershell
.\scripts\Install-Services.ps1          # publishes, installs 4 services (run as YOUR account), adds a Start-menu shortcut
.\scripts\Uninstall-Services.ps1        # removes them (-RemoveData also deletes %ProgramData%\EntireTeamWorkflow)
```

**Why the services run as your account:** the agents use the Claude sign-in stored in *your* profile (`%USERPROFILE%\.claude`).
A service running as LocalSystem would have no Claude sign-in (`-LocalSystem` exists, but agents then cannot start; the app shows a warning).
You are asked for your Windows password once; the script grants "Log on as a service" and sets restart-on-failure.

Data lives in `%ProgramData%\EntireTeamWorkflow`: `data\etw.db`, `secrets\` (DPAPI-encrypted sign-in tokens), `logs\` (daily files per service), `api.key`.

## How it works

1. **Sign in with Claude.** Exactly like the SSMS reference extension: the app runs the official `claude auth login` (browser OAuth) and polls `claude auth status`. This app never sees or stores Claude credentials. After sign-in you land on the workflow board.
2. **Create a project** (as many as you like; switch between them in the top bar). Two kinds:
   - **New app:** name, folder to build in, **backend language** and **UI framework** dropdowns (C#, Java, Python, Node, Go, Kotlin, Rust, PHP · Blazor, React, Angular, Vue, Svelte, Next.js, plain HTML).
   - **Existing repositories** (for teams with code already in Azure DevOps): pick the Azure DevOps **organization**, **project** and any number of its **repositories** from dropdowns/checkboxes, plus a workspace folder. The project keeps its own Azure DevOps link, so every project can use a different organization, project and set of repos, and its backlog is imported from that project. Change the repos later with **Repositories…** on the canvas. See *Existing repositories* below.
3. **Add user stories** on the **Backlog** tab — write them in the app, or import them from **Azure DevOps** (OAuth). New stories can also be pushed to Azure DevOps.
4. **Press Run.** The only moment a person is involved is right after the stories: an analyst agent may ask a few clarifying questions (once). Then the run continues unattended:
   - **Plan** — the architect writes `docs/architecture.md`, a full **OpenAPI contract** (`contracts/openapi.yaml`) and a task plan.
   - **Build in parallel** — backend, UI and QA engineers (and optionally a database engineer) run **at the same time** as a Microsoft Agent Framework *concurrent workflow*, all coding against the contract (the UI uses a mock layer until the backend exists).
   - **Integrate and test** — an integration engineer builds both halves, runs every test suite, fixes failures, writes the README and makes the first commit.
   - Optional steps from the **+** button: **Notify on Slack / Teams**, **Deploy to Azure**, **Custom agent**.
5. Watch progress live on the canvas (badges on each node and each engineer) and on the **Runs** tab (one lane per agent).

### Existing repositories

A project in this mode changes code that already exists instead of generating a new app. Every run:

1. **Prepares the repositories:** each is cloned into `<workspace>\<repo>` (or updated if already there) and a branch `etw/<run>-<story>` is created from the repository's default branch. A clone with uncommitted changes is never touched; the run stops and says so.
2. **Plans:** the architect reads the code of every repository and writes `.etw/docs/plan.md` in the workspace (outside the repositories): impact per story, agreements between repositories, and a section per repository.
3. **Builds in parallel:** one engineer per repository, each only inside its own repository, following its conventions and running its existing tests. The role engineers of the *New app* workflow are not used.
4. **Verifies:** the integration engineer builds and tests the changed repositories and checks the cross-repository agreements.
5. **Delivers:** right after the last step that can change code, each changed repository gets one commit (authored by the signed-in Azure DevOps user), the branch is pushed and a **pull request** is opened into the default branch, linked to the stories' Azure DevOps work items. The default branch is never touched directly. Repositories without changes get no pull request. The pull-request links appear in the run log and in Slack/Teams notifications.

Agents may not run `git commit/push/checkout/reset`; the pipeline owns git. Git authenticates with your Azure DevOps sign-in (an `Authorization: Bearer` header handed to git through environment variables), so no personal access token or credential manager is involved. Git for Windows must be installed. *Deploy to Azure* is not used for existing repositories: their own Azure DevOps pipelines deploy after the pull request is merged.

Agent configuration is per node: click a node to set its model/effort (`opus`, `sonnet`, `haiku`, `fable` or a full id), language, prompt, or Slack/Teams channel.
Settings: parallel agents (default 3 — each uses your Claude plan's capacity), timeout, permission mode, simulation mode.

## Connecting services

Every connection is a **Sign in** button on the **Connect** tab (or inside the step). Nobody pastes a client ID, tenant or URL. Sign-in happens in the desktop app (a service cannot show a browser) with OAuth authorization code + PKCE on a loopback port and no client secret; the tokens are then handed to the service, which keeps them DPAPI-encrypted and refreshes them.

**Azure DevOps, Azure and Microsoft Teams.** *Sign in with Microsoft* uses Microsoft's own public clients, so there is no app registration: the Azure CLI developer sign-on client for Azure DevOps and Azure, and the Microsoft Graph Command Line Tools client for Teams. Afterwards you pick from dropdowns: Azure DevOps organization and project; Azure subscription, resource group and region (in the *Deploy to Azure* step); Teams team and channel. Teams messages are posted as you.
Only if your organization blocks these built-in clients, use **Advanced: use your own app registration** on the card (redirect URI `http://localhost`, public client flows allowed; delegated permissions: Azure DevOps *user_impersonation*, Azure Service Management *user_impersonation*, or Microsoft Graph *ChannelMessage.Send*, *Team.ReadBasic.All*, *Channel.ReadBasic.All*).

**Slack.** *Sign in with Slack* uses Slack's PKCE flow for desktop apps (no client secret). Slack has no Microsoft-style shared client, so Slack needs **one Slack app, registered once**. The first press of the button walks you through it inside the card:
1. **Create the Slack app** opens api.slack.com with the whole app definition pre-filled (user scopes `chat:write`, `channels:read`, `groups:read`, PKCE on, redirect URLs `http://localhost:53682/` to `53685/`). Press *Create App*.
2. On the app's *Basic Information* page copy the **Client ID** (public, not a secret) and paste it into the card. The app remembers it (in the integrations service's encrypted store); from then on signing in is just the button.

Whoever ships the product can skip even that by setting `SlackOAuth.DefaultClientId` in `src\Etw.Shared\Auth\SlackOAuth.cs` (or the `ETW_SLACK_CLIENT_ID` environment variable): nobody is then asked for anything. To let people in other workspaces use the app, activate *Manage Distribution* for it in Slack.

Slack does not let desktop apps ask for bot permissions, so Slack messages are posted as the signed-in user, and only to channels that user is a member of. A person can be in several workspaces: *Add another workspace* signs in to one more (choose it in the browser), and the **Workspace** dropdown picks the active one, followed by the **Channel** dropdown of that workspace.

**Azure deployment (optional).** Add *Deploy to Azure*, pick subscription / resource group / region and an app-name prefix. A release agent produces production builds and `.etw/deploy/manifest.json`;
the Azure service then creates the resource group, a Linux App Service plan and one web app per component (`<prefix>-backend`, `<prefix>-frontend`) and uploads each build with your own Azure permissions. A plan costs money on your subscription. Leave the step out and nothing is ever deployed.

## Security notes

- Services listen on **127.0.0.1 only** and require the `X-Etw-Key` header (random key in `%ProgramData%\EntireTeamWorkflow\api.key`, readable only by SYSTEM, Administrators, the service account and the installing user). This also stops web pages from driving the API.
- **Agents execute code on your machine** with your rights: they edit files in the project folder and run the build tools of the chosen languages (`Bash(dotnet *)`, `Bash(npm *)`, …; permission mode `acceptEdits`). `npm run`/`pip install` can run arbitrary package scripts — use a dev machine or VM for untrusted ideas. The target folder may not be a drive root or a system folder.
- Agents run with `--strict-mcp-config` and `--no-session-persistence`, so your interactive Claude MCP servers and history are not touched.
- Secrets (OAuth tokens) are DPAPI-encrypted for the service account; the Claude credentials stay in Claude Code's own store.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| "backend is not running" banner | Start the services (`Start-Dev.ps1` or `Start-Service EtwBackend`). Logs: `%ProgramData%\EntireTeamWorkflow\logs`. |
| "signed in, but the backend service is not" | The services run as another account than you. Re-run `Install-Services.ps1` (it uses your account). |
| Access denied reading `api.key` | Re-run the installer as the same user that opens the app. |
| Run fails immediately: Claude CLI not found | Install Claude Code or set its path in **Settings**. |
| Agents hit usage limits | Lower *Agents working at the same time* in Settings, or choose a lighter model on the Claude node. |
| Azure DevOps "rejected the request" | Sign in again and check you can access the organization. With your own app registration it needs the Azure DevOps `user_impersonation` permission. |
| "Git was not found" when a run starts (existing repositories) | Install Git for Windows on the computer the services run on. |
| "…has uncommitted changes" before a run | The workspace clone of that repository has local edits. Commit or discard them, or choose another workspace folder. |
| "…was pushed, but the pull request could not be created" | Your Azure DevOps sign-in lacks rights to open pull requests in that repository, or expired. Sign in again; the branch is already on the server. |
| Microsoft sign-in says "approval required" / blocked | Your organization restricts the built-in clients: ask an admin to allow them, or use **Advanced: use your own app registration**. |
| "Could not open the live connection" right after starting | The backend was still starting. The app now waits up to 45 s for it; if it still fails, check the `EtwBackend` log and press **Retry**. |

## What has been verified

- All projects build with 0 warnings; the backend runs under the Service Control Manager (start, health, API key, DB, logs).
- Desktop app: the signed-out login screen, the canvas, inspector, backlog, connections, settings, runs, and a full **simulated** run through the UI including the question gate and parallel lanes.
- The Claude path with **real** agents: CLI flags validated, and a Haiku-driven plan stage wrote `architecture.md`, `openapi.yaml` and `tasks.md`.
- **Existing repositories:** the git mechanics were run for real against a local bare repository standing in for Azure DevOps (clone, branch, commit with the signed-in author, push, refreshing an existing clone, refusing a dirty clone, path and branch-name safety); a full **simulated** run through the Backend, Orchestrator and Integrations services (old databases are upgraded in place; plan, one engineer per repository, verify, pull-request step); and the new-app flow still passes.
- **Not exercised here** (needs your accounts): Azure DevOps OAuth/import, listing real repositories and opening real pull requests, Slack/Teams delivery, Azure deployment, a full multi-engineer real run, the interactive Claude sign-in itself (the machine was already signed in), and `Install-Services.ps1` itself (needs your password and changes the machine).
