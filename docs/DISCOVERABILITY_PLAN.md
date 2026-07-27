# Discoverability, README, and website plan

Planning baseline: 25 July 2026

## Recommendation

Improve the GitHub repository first. Do **not** build a standalone website yet.

The highest-return sequence is:

1. make the repository name/description/topics and social preview accurately describe the product;
2. make the first screen of the README answer **what it is, who it is for, what it cannot do, and where to download it**;
3. publish a tested compatibility table, privacy/safety information, and durable troubleshooting pages in this repository;
4. measure whether search and release traffic justify maintaining a distinct website;
5. use GitHub Pages or a small static site only when it adds unique, maintained material rather than duplicating the README.

This follows GitHub's guidance on [repository topics](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/classifying-your-repository-with-topics), [README content](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes), and [repository search](https://docs.github.com/en/search-github/searching-on-github/searching-for-repositories). It also follows Google's advice to write useful, original, people-first content with descriptive titles instead of keyword stuffing: [SEO Starter Guide](https://developers.google.com/search/docs/fundamentals/seo-starter-guide) and [title-link guidance](https://developers.google.com/search/docs/appearance/title-link).

## Positioning

### One-sentence product statement

> SteamP2PInfo is an open-source Windows tool that shows Steam P2P peers, ping, and connection quality for Elden Ring and compatible Steam Networking games, with an optional overlay, recent-player support, privacy-conscious history, and a manual exact-flow disconnect.

Use a shorter version where space is limited:

> Windows monitor for Steam P2P peers, ping, and connection quality in Elden Ring and compatible Steam Networking games.

### Claims to make

- Windows application.
- Shows Steam peers, ping, and connection quality when the game/API/transport exposes enough information.
- Made with Elden Ring in mind.
- Supports compatible games that authenticate peers through the required Steam calls.
- Optional windowed/borderless overlay.
- Can add peers to Steam's recent-player list.
- WPF v1.5 working tree includes local per-game history.
- Manual enforcement is exact-flow and refuses cases where a usable endpoint/ownership cannot be established.

Every capability still needs a current tested-compatibility label before it is described as universally supported.

### Claims to avoid

- “Fix Elden Ring lag.”
- “Reduce Steam ping.”
- “Solve Steam networking.”
- “See every player in every Steam game.”
- “Safe with every anti-cheat.”
- “Blocks only the selected player” without the documented shared Steam-flow boundary and test evidence.
- “No data leaves your PC” unless every update check, external link, Steam API interaction, and future diagnostic path has been audited and the statement is phrased precisely.
- “100% reliable,” “undetectable,” or similarly absolute promises.

The README can truthfully acknowledge searches such as **Elden Ring network issues** by saying the application helps inspect peer connectivity; it should immediately state that it does not repair an ISP route, Steam outage, Wi-Fi problem, game server, or relay.

## GitHub repository metadata

These settings are outside Markdown and therefore are proposed here rather than changed by this documentation-only task.

At the audit date, the public repository had no description, no homepage, and no topics. Filling those fields is therefore a higher-priority discovery action than adding more repeated keywords to the README.

### Repository name

Keep the existing repository name for now to avoid breaking familiarity and links. If a rename is later justified, prefer a stable product name such as:

`SteamP2PInfo-PingGuard`

Avoid version/status words such as `revised`, `new`, `fixed`, or `winui3` in the long-term product name. GitHub redirects old repository URLs after an ordinary rename, but release notes, screenshots, package IDs, update checks, and third-party links should still be audited before changing it.

### Suggested description

> Windows tool for monitoring Steam P2P peers, ping, and connection quality in Elden Ring and compatible Steam Networking games.

This is deliberately narrower than “fix network issues” and remains understandable without already knowing SteamP2PInfo.

### Suggested topics

Use a focused list rather than every adjacent gaming term:

`elden-ring`, `steam`, `steam-networking`, `steamworks`, `peer-to-peer`,
`p2p`, `ping-monitor`, `network-monitoring`, `latency`, `multiplayer`,
`windows`, `gaming-tools`, `etw`, `windows-filtering-platform`,
`winui3`

Do not add game names until that game appears in the maintained compatibility matrix.

### Other repository settings

- Set the website field only after a durable site exists.
- Create a current 1280×640 social-preview image showing the real application and readable product name.
- Pin the latest stable release and keep its asset name unambiguous. The v1.4.0 asset already follows the useful `SteamP2PInfo-v1.4.0-win-x64.zip` pattern.
- Add issue forms for bug report, game compatibility result, and feature request.
- Add `SECURITY.md`, a plain-language privacy page, support boundaries, and a compatibility page as future Markdown work.
- Keep Releases as the canonical binary source; distinguish release assets from GitHub's automatic source archives.

## Search-intent map

Use these phrases naturally in genuinely useful sections. They are subjects to answer, not strings to repeat.

| Search intent | Honest page/section | Question to answer |
|---|---|---|
| `Elden Ring ping monitor PC` | README title/opening and product page | What tool shows peer ping in Elden Ring on Windows? |
| `view Elden Ring player ping` | Features and quick start | What will appear, and what setup is required? |
| `Elden Ring network issues` | Troubleshooting guide | Which peer/transport facts can this tool reveal, and which problems can it not diagnose? |
| `Elden Ring ping -1` | Troubleshooting | What does unavailable ping mean, how can users distinguish ETW/API/relay/setup causes, and what diagnostic data is safe to share? |
| `Steam P2P connection monitor` | README title/opening | Which Steam calls/games are supported and why? |
| `Steam peer ping overlay` | Overlay section | Which display modes, capture modes, and accessibility limits apply? |
| `Steam IPC log not found` | Setup troubleshooting | Is the setting a file or folder, where is it usually stored, and how should custom installs be selected? |
| `Steam BeginAuthSession log_ipc` | Technical FAQ | Why is the command needed, what events are read, and what red console messages may be expected? |
| `Steam Networking ping monitor` | Compatibility/technical overview | How do old and new Steam networking API measurement paths differ? |
| `Steam recent players Elden Ring` | FAQ | How can detected peers be added/opened, and what remains controlled by Steam? |

Avoid creating thin near-duplicate pages for every phrase. One strong troubleshooting guide can answer several related intents.

## README information architecture

The main README should use this order:

1. Descriptive H1: **SteamP2PInfo — Elden Ring and Steam P2P Ping Monitor for Windows**.
2. Two-sentence product statement and limits.
3. Prominent **Download the app** link to the latest Releases page, with a warning that source archives are not the application.
4. Current screenshot with meaningful alt text.
5. **What it shows** and **What it does not do**.
6. Supported platform/game/transport limitations.
7. Five-step quick start.
8. Feature summary.
9. Natural troubleshooting headings:
   - Elden Ring peers or ping are not detected
   - Steam IPC log file is not found
   - Ping shows Unavailable or `-1`
   - Overlay is not visible or OBS cannot capture it
   - Manual disconnect is unavailable or Steam Datagram Relay is active
   - Hotkey did not register
   - Why administrator permission is required
10. Privacy and network-safety boundaries.
11. History and manual-action detail.
12. FAQ, development/roadmap, attribution, licence, and non-affiliation.

The revised [README](../README.md) applies this structure while preserving the detailed current behaviour.

## Content backlog

Create these as maintained Markdown pages before a website:

1. **Compatibility matrix**
   - app version;
   - game and executable;
   - last tested game/Steam/Windows version;
   - Steam networking path;
   - host/client/game modes;
   - direct/relay outcome;
   - peer/ping/history/overlay/manual-action result;
   - known limitation and evidence link.

2. **Elden Ring and Steam P2P troubleshooting**
   - staged health check from Steam command through file, parser, peer, ETW, and UI;
   - direct versus relay explanation;
   - ping unavailable reasons;
   - overlay/display/capture checks;
   - safe redacted support-bundle instructions.

3. **Privacy and data**
   - exact stored fields and paths;
   - endpoint/Steam-ID treatment;
   - retention and Clear History;
   - update/network interactions;
   - redaction and deletion.

4. **Network enforcement safety**
   - monitoring versus manual action;
   - exact-flow and Steam-owned fallback boundaries;
   - relay/ambiguous refusals;
   - filter/session lifetime;
   - multiplayer penalty/misuse warning;
   - tested matrix and known limitations.

5. **Installation and release verification**
   - release asset versus source archive;
   - supported Windows/architecture;
   - UAC reason;
   - signatures and SHA-256 verification;
   - clean upgrade/rollback.

6. **Developer architecture**
   - IPC/Steam/ETW/WFP overview;
   - threat/privacy model;
   - fixture strategy;
   - WinUI parity status.

Each page should have a named owner or review cadence. Stale compatibility and security claims are worse than no claim.

## Website decision

### Do not launch one merely for SEO

A duplicate of the README adds maintenance, canonicalization, security, accessibility, and privacy work without adding user value. GitHub already supplies repository, release, issue, source, and trust context.

### Launch when at least three conditions are true

- Search traffic reaches GitHub but users do not find the right setup/troubleshooting answer.
- There are at least four durable pages that are materially different from the README.
- Someone will own dependency/security/accessibility/content updates.
- Signed releases and a stable update/rollback story exist.
- A custom domain and Search Console are acceptable operational responsibilities.
- The project needs a compatibility database or interactive diagnostic decision tree that is awkward in Markdown.

### Recommended first website

Use a static, progressively enhanced site on GitHub Pages or another low-maintenance host:

```text
/
/download/
/guides/elden-ring-ping-monitor/
/guides/steam-p2p-troubleshooting/
/compatibility/
/privacy/
/security/
/changelog/
```

Requirements:

- one canonical URL per subject;
- unique page title, H1, summary, and useful body;
- semantic HTML, keyboard access, responsive layout, high contrast, and minimal JavaScript;
- canonical links if repository text overlaps;
- permanent links to signed, immutable GitHub Release assets rather than copied binaries;
- no advertising trackers or invasive fingerprinting;
- a sitemap and robots file;
- Search Console for query/index coverage, not speculative keyword counts;
- `SoftwareApplication` structured data only when the visible page contains matching, current facts. Google's requirements are documented in [Software app structured data](https://developers.google.com/search/docs/appearance/structured-data/software-app).

Suggested home-page title:

> SteamP2PInfo — Elden Ring & Steam P2P Ping Monitor for Windows

Suggested meta description:

> View Steam P2P peers, ping, and connection quality in Elden Ring and compatible Windows games. Download SteamP2PInfo, read setup help, and check limitations.

Do not use the obsolete `meta keywords` field or hidden/repetitive text.

## Measurement

Establish a baseline before changing metadata, then review monthly:

- GitHub repository views and unique visitors;
- traffic sources and popular content;
- latest-release page views and asset downloads;
- README-to-release click rate where measurable without tracking individuals;
- repeated issue categories, especially “downloaded source,” IPC path, blank overlay, and ping unavailable;
- compatibility-page submissions;
- if a site exists, Search Console impressions, clicks, indexed pages, and queries;
- first-success survey or opt-in issue template, not silent application telemetry.

Success is not raw visits. Prefer:

- more correct release downloads;
- fewer setup/source-archive mistakes;
- fewer duplicate support issues;
- more reports containing the safe diagnostic fields needed to act;
- higher successful-first-attach feedback;
- no increase in misleading “fix lag” expectations.

## Editorial rules

- Write for a player first and a networking developer second.
- Put the answer before implementation detail.
- Use “Steam IPC log **file**,” not an ambiguous “path.”
- Use “ping unavailable” plus a reason instead of normalizing `-1`.
- State the tested app/game/Steam/Windows version beside compatibility claims.
- Date safety/privacy/compatibility pages.
- Link claims to tests, releases, or source where useful.
- Spell out Windows Filtering Platform once before using WFP.
- Keep Elden Ring and Valve/Steam trademarks factual; add a non-affiliation statement.
- Never use another project's logo or game artwork as if it were this project's identity without permission.

The accompanying README rewrite is the immediate implementation. Repository metadata and any website remain explicit future actions.
