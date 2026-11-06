[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.StartScreen>
[vsixgallery]: <https://www.vsixgallery.com/extension/StartScreen.2d76ac3d-7ff2-47e1-82d8-e507cf765bbe>
[repo]: <https://github.com/madskristensen/StartScreen>

# Start Screen for Visual Studio

[![Build](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/StartScreen/actions/workflows/build.yaml)
[![Install from VSIX Gallery](https://www.vsixgallery.com/badge/StartScreen.2d76ac3d-7ff2-47e1-82d8-e507cf765bbe.png)][vsixgallery]
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

Download this extension from the [Visual Studio Marketplace][marketplace]
or get the latest CI build from [Open VSIX Gallery][vsixgallery].

----------------------------------------------

**Your first five seconds in Visual Studio should feel *fast*.** Start Screen
replaces the default launch experience with a clean, modern dashboard that
gets you into code immediately - no clicking through menus, no waiting.

### Startup and background work

The action bar and search are available before secondary content loads.
One themed progress bar overlays the action-bar separator across the screen for
Dev Hub, provider sign-in, Copilot connection, news, and other background refreshes.
It counts completed authentication, activity categories, discovered repositories,
cache reads/writes, Copilot connection stages, and other section refreshes. Totals
expand as repositories are discovered; this is work-item progress, not an estimate
of remaining time. Its muted theme color and reserved space keep refreshes unobtrusive.
The header's last-updated caption shows the current step while work is running,
including account checks, provider activity, and Copilot discovery, then returns to
the last successful update time. Parallel operations continue independently; the
caption describes one active operation. Extra connecting and updating labels are
omitted; actionable failures and sign-in recovery links remain.
Recent projects become clickable without waiting for filesystem timestamps, existence
checks, Git metadata, or history writes. Those details are filled in afterward;
Your Work can load independently. Repository summaries, dashboard refreshes, and
news start after the UI has yielded to rendering and user input. Cache reads, feed parsing, and thumbnail
decoding run on background threads with bounded disk and network concurrency.
Local workspace metadata is published progressively by two background workers in
batches of up to eight, with selected and pinned workspaces scheduled first.
Repository summaries start as local metadata arrives, independently of remote Git
fetches. Copilot session and solution timestamp scans are shared within each pass
for workspaces using the same directories. Search keeps the existing bound list
and avoids resetting it when the matching items and their groups have not changed.
The saved recent-project column width is applied before the first render, so it
does not shift when background content loads.
Dev Hub does not read its cache or authenticate providers when disabled.
Recent projects use the host's code-container registry so its persistence serializer
remains compatible across Visual Studio versions. An initial MRU failure is logged
without preventing Dev Hub or news from loading. Azure DevOps identity requests use
the preview version required by the connection-data endpoint.

Each dashboard refresh discovers its repository context once and shares that
snapshot between personal and repository activity queries. Repository-detail
requests use a shared concurrency limit, including cancellation while queued.
Each provider starts fetching and publishing fresh activity as soon as its own
authentication finishes. GitHub does not wait for Azure DevOps or other providers
to sign in, and repository-scoped refreshes authenticate only the owning provider.
The final dashboard cache is saved after all providers finish; interim results
remain available while other providers are still connecting.
Cached personal and team activity is restored independently for each provider as
its current identity is verified, without waiting for other providers to finish
signing in. A slow or signed-out provider does not discard another provider's
verified cache. Account changes and activity-setting or repository-context changes
still prevent mismatched cached activity from being displayed.
News, video, dashboard, and Copilot history caches use bounded JSON reads and
atomic file replacement so interrupted writes preserve the previous snapshot.
Feed configuration edits are also published atomically, and concurrent add/remove
operations preserve each other's changes. Failed saves are reported to the caller.

Remote response bodies have cancellation, time, and size limits, including after
response headers arrive. RSS and Atom responses are limited to 4 MiB and reject
document type declarations. Summary HTML stripping uses a bounded-output linear
scan, including for malformed markup. Failed sources continue to use available
cached content rather than being reported as successfully refreshed.
Thumbnail downloads are limited to 2 MiB, decoded before atomic publication, and
invalid cached images are removed and downloaded again. The thumbnail disk cache
retains at most 100 images. Repository-scope activity caches evict older entries,
and changing scope releases hidden activity cards from the previous context.
Large activity reorders rebuild the visible collection instead of repeatedly
searching and moving items. Unchanged refreshes retain existing item instances.

News and videos have focused section view models that own their inventories,
filters, unread markers, and refresh state. Dev Hub activity presentation is
separate from panel event handling. The screen coordinates these sections while
preserving its existing bindings and stable item collections.

Hiding the Start Screen cancels refreshes and pauses its automatic refresh timer.
Closing the tool window permanently releases its event subscriptions, timers,
and background-work resources; reopening creates a fresh screen instance.
Visual Studio service health is rechecked automatically every five minutes while
the screen is visible, including retries after failed checks. The indicator starts
visible and green while the initial background check is pending, avoiding a layout
shift when services are healthy. Its tooltip identifies this initial assumption.
Checking again preserves the current indicator until a result arrives. Failed or stale
results are shown explicitly rather than continuing to report healthy services.
Visual Studio shutdown also cancels background work and pending dispatcher waits,
without waiting for the Start Screen to unload or hiding it synchronously.
Background Git fetches suppress credential prompts, honor cancellation, and time
out after five seconds. Reopening the screen does not retry the same repository
more than once every fifteen minutes, including failed fetch attempts.
Git pull is noninteractive, supports cancellation, and has a two-minute deadline.
Credential operations also have cancellation and deadlines. Their
output streams are drained concurrently with bounded retained diagnostics, and
cleanup terminates owned helper processes as well as Git. Account enumeration is
shared while settings are open instead of starting another process on every
dashboard update. Credentials acquired before an account invalidation cannot
repopulate the cache afterward.
Rejected-token retries invalidate only the affected provider's credentials, not
other providers' in-flight sign-ins or verified dashboard snapshots. Explicit
account changes still invalidate the dashboard and repository scopes.
Automatic Bitbucket authentication runs only when a Bitbucket repository is in
the workspace or a matching credential exists in Start Screen or Windows Git
Credential Manager. Detection checks Git credential target names without reading
their passwords. Other credential-store backends require a matching repository
or a token stored through Start Screen. Bitbucket failures are logged through the
VSIX Toolkit instead of the activity warning banner.
The Dev Hub account settings include independent GitHub, Azure DevOps, and
Bitbucket checkboxes. Turning off a provider stops its authentication, activity,
and repository-summary requests without deleting its stored credentials.
Git status detection uses version-matched native libraries bundled with the
extension for x64 and ARM64 Visual Studio. No separate libgit2 installation is
required.

The **Start Screen** Output pane shows readable progress and recovery guidance,
without raw exception details or boolean status dumps. Unchanged refresh status
messages are not repeated as activity sources finish loading. Exceptions are
logged with the VSIX Community Toolkit's `ex.LogAsync()` in the **Extensions**
Output pane, keeping stack traces separate from Start Screen status messages.
Activity HTTP failures identify the endpoint path without query parameters.
Legacy Azure DevOps `DefaultCollection` remotes resolve to their actual project
and repository. Personal project queries coalesce modern and legacy cloud URLs,
and activity merges use Azure DevOps item identities so cached and fresh URL
aliases do not produce duplicate Attention cards. PR metadata requests use the
repository GUID returned by Azure DevOps when available.
Sign-in recovery links open **File > Account Settings** for Visual Studio-backed
Azure DevOps and Feedback authentication. GitHub reconnect uses Git Credential
Manager. Organization SAML SSO restrictions on optional metadata are logged only
in the Output pane, not the dashboard warning banner. The GitHub account settings
include an optional organization-authorization link, since permissions vary by
organization and do not prevent using the rest of Start Screen.

For startup profiling, Debug builds emit `[StartScreen startup]` trace milestones
for control creation, loading, MRU display, and secondary content. These are
diagnostics, not a startup-time guarantee; compare cold and warm VS launches,
including offline and large-repository scenarios, in the Performance Profiler.

![Start Screen screenshot](art/startscreen.png)

## Get to work

Open a recent solution, create a new project, open a project or folder,
clone a repo, or attach and reattach to a process - all from a single,
focused screen. The action bar puts every common workflow front and center
so you never have to dig through File or Debug menus again.

## Your recent projects, supercharged

Start Screen doesn't just list your recent files. It understands how you
work:

Recent projects and folders come from Visual Studio's current Recent Projects
list, even when they are missing from the legacy MRU store. Removing or clearing
entries also updates that list.
Recent items scroll independently of Your Work and news, with a thin, discreet
scrollbar. The right pane has its own subdued scrollbar, so browsing recent items
keeps the selected workspace's details in view.

**Find anything instantly.** Type a few characters and the search filter
narrows your list in real time. Pin the projects you care about most so
they always float to the top.

**Preview before opening.** Select a recent workspace to show its repository
activity in the center pane. Double-click or press Enter to open it. Choose
**All repositories** to return to your combined activity. **Solutions only**
filters unpinned entries; pinned folders and projects remain visible, and
search still applies to them.
Selecting a workspace without a supported Git remote clears any previous
repository error and shows its local work without treating it as a failure.
The action buttons and **All repositories** use Visual Studio's native themed
button style, including hover, pressed, disabled, and keyboard-focus states.
Settings disclosure headers use theme-aware text and chevrons, including when
the Visual Studio theme changes while settings are open.
Account, default-tab, and sort selectors share Visual Studio's native themed
ComboBox style rather than the default Windows control template.
All dashboard sections share semantic typography and spacing resources, scaled
from Visual Studio's environment font. Card titles, secondary metadata, section
headings, status badges, filter selectors, and icon actions use common styles
while recent projects, activity, news, videos, and feedback keep their distinct
layouts. News and video cards share click and keyboard activation rules and
live-themed hover/focus behavior. Recent projects retain selection and
double-click opening.

Arrow keys move between adjacent activity blades and News & updates. In activity,
Up/Down move between the heading, selected filter, and cards; Left/Right select
filters and move to the neighboring blade at the ends of the filter row. In News,
Left/Right navigate the selected tab's card columns, and Up/Down navigate rows.
Up from the first row returns to the tab header; Left/Right on headers select
tabs, and Down enters their content. Left at the first column or first tab
returns to Team activity. This also works for Feedback and Release notes, and
empty tabs remain reachable through their headers.

**See real repository activity.** A small PR icon and count beside the branch show
repository-wide open pull requests for supported GitHub and Azure DevOps repositories.
Zero counts are hidden. Recent items use compact 32-pixel single-line rows;
direct personal review requests add a second line only when present.
Counts are fetched for all visible recent workspaces, independently of the limited
dashboard lists. Repository discovery uses each workspace's local Git remote, even
when checkouts are outside Visual Studio's default projects location.
Only positive counts are shown; loading, unsupported, signed-out, and unavailable
states add no placeholder links or status text to recent items. Select a PR count
to filter both ownership views to PRs without changing your selected view, or
select the workspace to view its repository activity. Select a personal review
count to open My work's Attention feed.

**My work** tracks your loaded open PRs, including drafts and requests for your review, created or assigned
issues and tasks, personally triggered builds, and local Copilot sessions. **Team activity** shows other people's
PRs and issues plus repository build activity, excluding requests for your review.
Team activity defaults to **PRs** and has no Attention tab.
My work defaults to **Attention**, a mixed-type feed of all loaded
items with a known action for you, ordered newest updated first without type
grouping or a separate top-items limit. The count shows how many items need you.
Type filters show their full inventories independently of Attention. There is
no separate Needs attention checkbox. Switching filters reuses each filter's
existing cards when its data is unchanged, instead of rebuilding them on every
tab visit. Hidden lists synchronize before display when their data changes.
Items stay in their ownership view rather
than appearing in a duplicate action blade. The two work blades sit side by side, with News to their right.
Smaller windows use horizontal scrolling rather than hiding a work blade or
moving News below it. Both views show colored review/check
status pills and a friendly update date, with the exact local date and
time on hover, plus background-free globe and pull request icons to open a PR in
your browser or Visual Studio. These action icons appear only while their entire
card is hovered or contains keyboard focus; their space stays reserved to avoid
layout shifts. When a workspace is selected, its name and **All repositories**
sit beside the My work heading, keeping both blades' filter rows aligned.
PR authors appear beside the status, with dates right-aligned on the repository row; PRs have no separate
action-text line or check-details link. Issues and tasks assigned to you have a
themed **Assigned to you** pill below the repository row. Labels use solid
rounded pills with their GitHub colors and contrasting text, with system colors
in High Contrast mode. All labels, including priority and type labels, appear
together in one row, wrapping only when needed; there is no separate priority/type row.
Open issues omit the redundant open-status pill. Meaningful workflow states remain
visible. Status colors distinguish failures/conflicts (red), success/approval
(green), review/discussion attention (amber), in-progress states (blue), and
drafts or cancelled activity (muted), using Visual Studio theme resources.
Selecting a workspace scopes these lists to that repository.

If optional GitHub review or check metadata cannot load, the warning includes the
underlying GitHub error. Credential, permission, and rate-limit failures have
specific recovery guidance instead of repeatedly recommending Refresh. Available
PR inventories and unaffected metadata remain visible; a successful refresh clears
the warning.
An organization SAML restriction on optional metadata does not mean GitHub is
disconnected. Authorize the existing OAuth app for that organization under
**GitHub Settings > Applications > Authorized OAuth Apps**. Refresh alone cannot
grant organization access; the loaded pull requests remain available meanwhile.

**Load more** shows loading feedback and reports when no additional activity is
found. With a workspace selected, it refreshes that repository directly.
**Include completed work (14 days)** optionally shows your recently merged PRs
and completed issues and tasks, separately from your active work.

**Connect your branch to its work.**
The MRU tooltip also connects the current branch to matching open PRs in already
loaded activity, showing review/check status and explicit linked work items when
the provider supplies them. Matching requires the source repository and exact
branch, so similarly named branches in forks are not confused. Checks identify
whether their commit matches local HEAD; unknown and different commits never
claim to validate local changes. Detached checkouts are not matched by branch.
Only two PRs and two linked items per PR are previewed, with additional counts.
Cached or incomplete activity is labeled, and no match is not proof of no PR.
Hovering does not authenticate, fetch data, or invoke Copilot. Existing row-level
branch and repository-wide counts are unchanged.

When a matching workspace is actually opened in this Visual Studio instance,
Start Screen saves a small activity checkpoint only if its loaded data is fresh,
verified, and complete. Selecting or hovering an MRU item never advances it.
On return, the tooltip can show up to four known changes since that visit: PR
status, checks, unresolved-discussion counts, head commits, and workflow states of
explicitly linked work items already loaded in Dev Hub. Missing items are not
assumed completed, newly discovered PRs are labeled as newly present in loaded
activity, and unknown metadata is not interpreted as a change. Comparisons require
the same workspace path, repository, branch, accounts, query, and activity limits.
Cached or incomplete comparisons are labeled and cannot replace a checkpoint.
An empty comparison makes no claim that nothing changed. Checkpoints retain at
most 500 workspace paths locally and contain no conversations or review text.
Opens in another VS instance are recorded only if Start Screen is loaded there.

**See your Git status at a glance.** Every repo shows its current branch,
ahead/behind counts, uncommitted changes, and the last commit time - all
loaded in the background so the UI stays snappy. Hover for the full
picture, or spot a detached HEAD before you accidentally commit to nowhere.

**Stay organized.** Projects are grouped by when you last touched them -
*Today*, *This week*, *This month* - so yesterday's prototype doesn't bury
this morning's deadline.

**Right-click for power moves.** Open the containing folder, launch a
terminal, pull the latest Git changes, copy the path, pin, unpin, or
remove - all from a context menu with keyboard shortcuts.

![Context menu](art/context-menu.png)

## Developer news, built right in

A curated feed of engineering news from the Visual Studio Blog, .NET Blog,
and more lives right next to your project list. It refreshes in the
background and caches locally, so it's always ready and never slows you
down.

News cards reserve two lines for titles and four for descriptions, with ellipses
for overflow and full text on hover. Pinned and unpinned cards use the same layout;
their heights scale with Visual Studio's environment font settings.

The Refresh button checks every enabled blog feed even when its local cache is
less than four hours old. Unchanged feeds can be validated without downloading
them again. Automatic refreshes continue to reuse fresh caches.
Feed caches are identified by source URL rather than display name. Renaming a
feed preserves its cache, changing its URL does not reuse the previous source,
and combining cached articles does not extend individual source freshness.
The header caption stays brief: the active refresh step, "Updated just now", or the
known update time. Before a successful update is known, it says "Not updated yet". Hover over
the status for per-section details, including cached content, unavailable sources,
disabled feeds, and retry guidance. If a blog source fails, available cached
articles remain visible. Cached or partially refreshed news is not reported as
fully updated.

Want different sources? Drop your own RSS or Atom feeds into a simple JSON
file and Start Screen picks them up automatically - no restart needed.
This file is the source of truth for custom sources and enabled feeds. Unused
legacy `CustomFeeds`, `FeedSelection`, and `LastFeedRead` options have been
removed; existing file-based feed preferences, pinned articles, and per-tab
read timestamps are preserved.

```txt
%USERPROFILE%\.vs\StartScreen\newsfeeds.json
```

```json
{
  "name": "My Custom Feed",
  "url": "https://example.com/feed.xml",
  "enabled": true
}
```

Full JSON schema validation is included so you get IntelliSense while
editing.

## YouTube video feed

The latest videos from the Visual Studio YouTube channel are shown in
the Videos tab of the News panel, right next to the Blogs tab. Each
video displays a thumbnail, title, and publish date - videos published
in the last three days get a "NEW" badge so you never miss fresh
content. A green dot appears on the Videos tab whenever new videos
arrive in a refresh, and clears once you view the tab.

The feed is cached locally and refreshes automatically every four hours.
Click the Refresh button at the top of the screen to force an update at
any time.

## Your Developer Community feedback

The **Feedback** tab in the News panel lists Developer Community tickets you
created or currently upvoted, with status, vote count, and last update date.
Ticket titles use the normal themed card text color, with compact muted metadata.
No created/upvoted count summary is shown above the cards; loading, empty, and error messages remain visible when applicable.
State pills use green for fixed/completed, blue for consideration/review, crimson
for more information needed, brown for investigation/roadmap, and gray for closed
or other states. New and triaged tickets are also blue. Short API state names are
shown as readable labels, including **Not enough info** and **Closed - Duplicate**.
Labels also respect the ticket type: suggestions show **Under Review** where
problem reports show **Under Consideration**. A pending fix is green and displays
**Fixed - Pending Release** for problems or **Completed - Preview** for suggestions.
Released suggestions display **Completed - Release**. Older cached tickets without
type information are refreshed before display instead of guessing their labels.
High Contrast mode uses system colors instead.
Select a ticket title to open it in your browser. Tickets you both created and
upvoted appear only once. All activity pages are checked; canceled votes and
comment reactions are excluded unless you also created or currently upvoted the
ticket. Deleted or inaccessible tickets are omitted.

Feedback participates in the normal background startup refresh, without waiting
for tab activation, using your signed-in Visual Studio account. Choose the same account
you use on Developer Community under
**Tools > Options > Azure Service Authentication**. No PAT or separate sign-in
is needed. Use the main **Refresh** button to reload after changing accounts or
to retry an unavailable service. Switching away from Feedback and back keeps
the displayed tickets without reloading. When reopening the Start Screen,
feedback first displays a verified account's cached tickets, retained for up to
seven days across screen instances and Visual Studio sessions. The normal startup
refresh then always requests the latest tickets and replaces the disk snapshot
after a successful download. New sessions and renewed credentials verify identity
before reading the account's disk snapshot. The main **Refresh** button bypasses
cache-first display and reloads feedback alongside the other data sources.
Hiding the screen cancels pending requests.

Feedback tokens are never written to disk. Ticket snapshots are isolated by the
authenticated Developer Community identity and encrypted on disk for the current
Windows user. A transient startup refresh failure keeps verified cached tickets
visible and leaves the disk snapshot intact; authentication failures clear the
displayed tickets. Feedback failures are shown in the tab and in refresh-status
details rather than presented as an empty result. Reopening the screen loads
cached online activity concurrently with the other background refreshes, rather
than waiting for news, videos, or feedback to finish.

Open **View > Output > Start Screen** for diagnostics. The pane uses `Vsix.Name`
and records timestamped refresh summaries, source counts, cache decisions,
and SAML details without automatically activating the window. Handled exceptions
are logged separately to the Toolkit's **Extensions** pane.
MRU retrieval is separate and unchanged.

## Drag and drop

Drop a solution, project, or folder from File Explorer directly onto the
Start Screen to open it. You can also drag pinned items to reorder them -
a visual indicator shows exactly where the item will land.

## Dev Hub

The Dev Hub panel shows your open pull requests, assigned issues, and
recent builds from GitHub and Azure DevOps - right next to your project
list. Data loads in the background and is cached locally so the UI stays
responsive. On later opens, the saved dashboard populates both work blades only
after the current provider identities and activity query/options have been verified.
Account, repository-context, query, item-limit, or completed-item setting changes
discard mismatched snapshots, including ownership flags. Older unpartitioned caches
are ignored and rebuilt. A cache older than 15 minutes, or one containing failed
sources, refreshes in the background. First use without a matching cache still needs
to fetch data. Additional repository-scoped details are retained in memory only and
never replace the aggregate dashboard or its disk cache.

Each source reports success, partial, unavailable, or unauthorized separately. A
successful empty category clears its old items; failed sources retain previously
loaded items only for the same verified identities and query context. Repository
categories are independent, so a failed issues request cannot keep successfully
emptied PRs or builds. Warnings and the refresh status identify stale or incomplete
activity, and these outcomes survive cache reloads. If authentication cannot be
verified, activity from a previous identity is not displayed.
Providers without configured accounts do not produce authentication warnings.
Unavailable optional GitHub review/check details are reported separately from
the successfully loaded pull request inventory; they do not imply a sign-in
failure or keep pull requests that are no longer in the refreshed inventory.

Completed Dev Hub categories appear without waiting for slower per-repository
activity. GitHub CI queries run with at most three repository requests in flight.
Verified identities are shared across activity and repository summaries for up to five minutes,
scoped by credentials and identity endpoint, and invalidated when authentication
changes. Failed identity lookups are retried rather than cached as signed-in users.

Both work blades are always visible. Keyboard navigation initially targets
**My work** by default. Existing Overview/Needs you preferences migrate
to My work, and existing Issues/Pull requests/Builds preferences migrate to
Team activity. The initial keyboard destination remains configurable. Work stays visible after viewing it;
activity timestamps do not determine ownership. Open PRs you authored, are assigned,
or are requested to review stay in My work's **PRs** tab regardless of age.
**Attention** requires activity in the last 30 days and explicit signals: requested reviews, changes requested
on PRs you authored or are assigned, known failing checks or merge conflicts on those PRs, assigned open
issues/tasks, unresolved discussions on your PRs, verified
merge readiness. Recency uses the last update, falling back to creation when unavailable;
items without either timestamp and dormant items remain in their normal tabs, not Attention.
Live Copilot questions or permission requests also appear in Attention, independently of this date window. Pending checks,
approvals alone, PR assignment alone, mentions, idle sessions, and generic updates do not imply an action.
Standalone builds, including failures, appear only in the build inventories, not Attention.
Unknown provider metadata is not treated as an action signal. A mention is not an
unread indicator, and an unresolved discussion is not proof of an unanswered question.
Developer Community feedback is not included.

My work and Team activity display cards directly below the selected tab without redundant
section headings. Tabs use a footprint-neutral underline for selection, without
surrounding borders or hover/selection backgrounds.
My work orders its tabs as Attention, Copilot, PRs, Issues, and Builds. On initial
load, empty Attention falls back to the first non-empty tab in that order. Later
refreshes preserve your selection. If every tab is empty, Attention stays selected.

Work cards show the title first, with the repository or Copilot workspace directly
below it. Work and Feedback dates align at the far right. Blog and video dates
stay on the left with their source metadata, and their titles use the normal theme text color.
Blog cards use the same padding as work cards and do not stretch to fill taller rows.
Copilot source icons sit beside the workspace name rather than occupying their own row:
Agent for Visual Studio (GitHubCopilot on older VS hosts), VisualStudioCode for VS Code (VisualStudioOnline on older VS hosts),
Console for CLI, and GitHub for cloud agents.
Hover over a source icon for its app name (Terminal, VS Code, Visual Studio, or GitHub).
VS Code sessions use the native workspace folder name; chats without a workspace show
**No workspace** instead of the internal chat-folder GUID.
Work card titles stay on one line with ellipsis; hover to read the full title.
Work card titles reserve extra space on the right to separate the blades visually.
Assigned issues/tasks show an **Assigned to you** pill below the repository row.
Cards keep one primary status without a separate PR action-text line or large,
redundant metadata tooltips. Open the item for complete provider details.
Issue label pills stay together in one row, wrapping when needed, directly beneath
the repository or status row without an empty action row. Click a card to open it;
PR cards also retain their browser and Visual Studio action icons.
The workflow state remains visible even when the item is assigned to you.
Build headings show the repository without branch names; hover over the repository
name for the full build branch and failure details.
Completed builds show compact durations when timing data is available. Azure DevOps
uses start and finish times, excluding queue time; Bitbucket uses its reported elapsed
duration rather than billed build seconds. GitHub shows elapsed time from the run's
start to its last completed update. Missing, invalid, and unfinished timings are hidden.
A PR is only called
**Ready to merge** when the provider supplies verified readiness; approval or a
passing build by itself is not enough. Missing or incomplete policy/discussion
information never becomes a ready signal.

The footer states the current per-query limit. **Load more** expands it in steps
of 30, up to 300, for the current page visit, including repository-scoped views.
Providers page their queries rather than requesting an unsupported page size.
These are recent, bounded activity lists, not complete repository inventories;
permissions, discovered projects, and provider availability still limit coverage.
**Recently completed** optionally adds a separate My work group for your merged
PRs and completed created/assigned issues or tasks from the last 14 days. It does
not replace the active-work queries or add completed work to Attention.
Copilot also discovers recent CLI and supported native VS Code sessions from their
local metadata. GitHub cloud-agent tasks use the configured GitHub account when that
provider is enabled. These sources refresh independently once a minute while the page is visible.

PRs open in Visual Studio's native review experience when the installed Git
tooling supports it. **Review on the web** remains available from the PR context
menu; unsupported providers use the browser. Native review failures are reported
instead of silently opening a browser. Git tooling is loaded on demand, so review
can be opened from the Start Screen before a repository is active.
Opening a review does not silently check out a branch.

Right-click an activity card (or press Shift+F10 while it is focused) for native
Visual Studio context-menu actions. PR cards use **Review in Visual Studio** and
**Review on the web** instead of inline action icons. Open GitHub.com PRs also offer
**Request Copilot Review**: after confirmation, this requests a review posted on
GitHub using the selected Dev Hub GitHub account. The account needs pull-request
write access and Copilot code review availability; the request may consume Copilot
usage. A successful request is not a completed review. Azure DevOps Services has
Copilot code review in public preview when enabled by administrators; use
**Review on the web** and its Copilot Request action there. Direct API requests from
Start Screen are currently supported only for GitHub.com.

Issue and task cards offer **Fix with Copilot**, which opens a new local VS chat
and sends a prompt beginning with `Fix <issue URL>`. Failed build cards offer
**Investigate with Copilot**, including the build URL, branch and failure summary,
and a failure-log link when supplied by the provider. These actions use the current
VS workspace and ask Copilot to verify the repository before making changes.
They do not clone repositories, switch branches, or grant tool permissions.
Start Screen remembers the most recent conversation it successfully started for
each issue or exact failed build run. Its action becomes **Continue in Copilot**,
which verifies the conversation in the current VS backend and opens it without
sending another prompt. **Start new Copilot chat** remains available. If the saved
conversation is absent from the current catalog, starting a replacement requires
confirmation. Associations are bounded to 500 entries and stored locally alongside
Copilot metadata; they contain item URLs and conversation IDs, not chat messages.
Access to issue details and private build logs depends on the tools and accounts
available to Copilot. The optional, version-sensitive VS harness contracts must
be installed; unsupported contracts and failed requests are reported explicitly.

An experimental, optional Copilot integration observes the installed VS harness
through its in-process MEF contracts. My work includes running and recent
conversations; its Attention feed includes sessions with unresolved
permission requests or questions, including requests from subagents. Clicking a conversation, or pressing
Enter on it, opens it in the app where it was created. Visual Studio conversations
open in its Copilot Chat, supported VS Code conversations open in its native Agents
window, CLI conversations resume in an interactive CLI console, and cloud tasks open
on GitHub. No prompts are sent and requests are never answered automatically.
Right-click, the Menu key, or Shift+F10 opens native Visual Studio session actions.
Only Visual Studio-to-CLI and VS Code-to-CLI alternatives are offered. There are no
destination buttons on cards and no unverified cross-IDE or CLI-to-IDE actions.
CLI opening requires an explicit final shutdown, no in-use markers or held fork lock,
and no known live activity. A retained, unheld fork-lock file does not block resuming
and is never modified or deleted. Native VS sessions do not have to be transcripts: saved, unhydrated
sessions can qualify, while a live VS activity stream blocks opening even when idle.
VS Code-to-CLI also requires VS Code to be closed. The checks run again
immediately before launch. Unknown ownership disables the action; locks are never
deleted, sessions are never forcibly taken over or silently forked. Active CLI
sessions cannot be focused externally and must be closed normally before resuming.
An idle session is not considered released. CLI must be installed as copilot.exe on PATH.
Conversation titles occupy a single line with an ellipsis; hover to see the full
title. The source icon appears beside the workspace below the title,
with the date aligned to the right. A compact, theme-aware status badge appears
below the workspace. Running uses the accent color, Waiting uses the warning color, and Idle
is muted. Conversations without a known live state show no state badge rather
than claiming to be idle. These signals do not describe work in
another Visual Studio instance or a separate CLI process.
Background Copilot diagnostics appear in the Start Screen Output pane, not beside the work heading.
Sessions appear as they are discovered, without waiting for the entire catalog.
Reopening the page reuses up to 100 conversation metadata entries while reconnecting.
Metadata is also saved locally under `%LOCALAPPDATA%\StartScreen\CopilotCache` after
discovery and when leaving the page. It contains conversation IDs, titles, working
directories, timestamps, and saved-conversation markers, not messages or live state.
On a new VS launch, cached metadata is loaded off the UI thread and bound before
Copilot service initialization, with a dispatcher yield to allow rendering first.
The first launch without a cache still depends on VS discovery. Cached entries show
no state badge and never retain old running or permission state. Completed discovery
replaces stale entries, including deleting history when the catalog is empty.
Cache errors are logged to Output without disabling
live discovery. Opening a cached entry requires the current backend to confirm
that the conversation still exists.
Workspace selection filters sessions by their reported working directory; sessions
whose directory is not yet known are not attributed to a selected workspace.

These are internal, version-sensitive contracts, not a supported Copilot SDK API.
The harness does not expose a force-relist operation. Refresh reconnects to its
current in-memory catalog; it cannot force saved history from other processes to
be re-listed, or obtain their live activity. Missing or incompatible contracts
produce an explicit unavailable status; discovery
in progress is not reported as an empty successful result. Refresh retries the
connection. Subscriptions are removed while the page is hidden or closed. Copilot
activity remains accessible without a GitHub/Azure DevOps dashboard connection.
Local discovery reads at most 100 recent CLI/VS Code metadata directories from
`%USERPROFILE%\.copilot\session-state`. VS Code's read-only native session catalog
must map the backing runtime ID to a native Agents session ID; unsupported legacy
formats and unmapped sessions are not shown. Creator metadata determines the icon,
not the last app that resumed a session. No transcripts are scanned for discovery.
The catalog is read through `Microsoft.Data.Sqlite.Core` using Windows' built-in
SQLite library, so the VSIX does not ship native SQLite binaries. This still
depends on VS Code's internal catalog format; incompatible metadata is reported
without changing the database.
GitHub cloud discovery requires Agent tasks read permission and queries up to 100
recent tasks. Synced local tasks are not treated as cloud agents. If the preview API
omits cloud-origin metadata, unverified tasks are omitted with a diagnostic in Output.
Cloud metadata is held only in memory and scoped to the current account. Cloud tasks
filter by repository; local sessions filter by working directory. Failure of one
source does not disable the others. Local files and open-session counts are never
treated as proof that an agent is running. No Copilot implementation assemblies are
shipped or required to build the extension.

Closing the Start Screen cancels pending Dev Hub requests without logging
normal cancellation as an error or caching canceled requests as empty data.
HTTP timeouts and other request failures are still logged for troubleshooting.

### Azure DevOps sign-in

Azure DevOps activity appears alongside GitHub in **My work** and **Team activity**.
The dashboard discovers Azure DevOps repositories from your recent workspaces,
not every repository accessible to your account. Open a checkout in Visual Studio
so it appears in the recent list, then refresh Start Screen. Recent builds include
failed PR validation runs; the combined dashboard initially loads up to 10 items per repository,
and scoping to a repository loads up to 30. Load more expands the query limits.
Signing in alone does not enumerate all
organizations or projects.
Personal PRs and created or assigned work items are queried independently across
projects discovered from recent workspaces, with up to three project requests
in flight per source. Personal PR queries include repositories in those projects
even when the individual repository is not in the recent list. They load up to
30 authored open PRs per project initially, separately from the repository-wide feed;
repository scopes also query authored open PRs independently.
Personal review requests are queried at project level as well, so they are not
limited to the latest PRs in individual recent repositories. Team work items
use a separate query from your created/assigned work. Available workflow categories
identify completed custom states; custom workflow coverage depends on the state
metadata exposed by the project. Recent ADO completion history requires a reliable
closure date. GitHub mentions are informational; Azure DevOps mentions are
not currently collected.
Project-level work items remain in the combined view; repository scopes include
only work items with an explicit link to that repository. Work Items (read)
access is required. If that optional lookup fails, accessible PRs and builds
remain visible with an issue-activity warning.
Queued Azure DevOps builds appear as pending using their queue time until they start;
running and completed builds use their start and finish times respectively.
Optimized Azure DevOps Git remotes (`/_git/_optimized/Repository`) are normalized
to the underlying repository for activity and build queries.

Azure DevOps Services supports **Visual Studio account (Microsoft Entra ID)**
authentication, including legacy `organization.visualstudio.com` URLs. No PAT,
extension-specific app registration, client secret, or hosted website is required.
In Dev Hub settings, choose **Use Visual Studio account (Entra ID)**. Sign in or
reauthenticate in Visual Studio first, and select the intended account under
**Tools > Options > Azure Service Authentication**. The extension reuses that
selection; it does not provide a separate VS account picker.

The organization's tenant is discovered before requesting a token, including
guest-tenant access. VS sign-in, organization permissions, and tenant policies
still apply. Short-lived access tokens are cached only in memory and renewed
before expiry. A 401 invalidates the credential and is retried once; background
refresh never opens a sign-in prompt. Failed authentication is shown as requiring
sign-in, not as zero repository activity.

**Use a PAT...** remains available where organization policy allows it. Existing
stored PATs remain in use until you explicitly select VS authentication. PATs
need Code (read), Work Items (read), and Build (read) permissions for the respective
features. Azure DevOps Server (on-premises) retains its existing credential/PAT
flow; cloud Entra tokens are never sent to these servers.

The selected cloud authentication method persists across VS restarts. Choosing
**Disconnect** stops Start Screen from reacquiring a cloud credential; it does
not sign you out of Visual Studio or affect other extensions.

![Dev Hub](dev-hub.png)

### Scope to a repository

Right-click any pull request, issue, or build and choose **Scope to
&lt;repo&gt;** to filter both ownership views down to that single
repository. Selecting a recent workspace scopes the same view. While scoped,
choose **All repositories**, or right-click an item and choose **Show all
repositories** to clear the scope and see everything again.

The scope is not saved between sessions, so reopening the Start Screen
always starts with the full view.

### Team activity search query (GitHub)

By default team activity shows issues and pull requests that "involve" your GitHub
account. Click the gear icon next to the Dev Hub header to enter a custom
GitHub search query under **Team activity search query (GitHub)**. Advanced connection
and search options are collapsed by default to keep account status and display
preferences easy to scan. Expand **Connection options** to change Azure DevOps
authentication, enter a PAT, or add a server. The extension prepends `is:issue` or `is:pr`
automatically, so you only need to provide the filtering part.

Use the `{login}` placeholder to reference the authenticated username.

**Example - show open issues across multiple orgs:**

```text
state:open archived:false sort:updated-desc user:madskristensen org:VsixCommunity org:ligershark
```

**Example - only issues assigned to you:**

```text
state:open assignee:{login}
```

**Example - issues in a single org:**

```text
state:open org:dotnet
```

Leave the field empty to restore the default behavior (`involves:{login}`).

The custom query controls all **Team activity** issue and pull request discovery,
including review requests, mentions, and the **Attention** view. It also applies
when a repository is selected. **My work**
independently queries your authored open PRs and created or assigned open issues,
including contributions outside that query's repositories and organizations.
An explicit workspace/repository selection still scopes both views. Build
activity always fetches from your recently pushed repositories and is
not affected by the query.

> The custom search query setting only applies to GitHub. Azure DevOps
> uses dedicated REST APIs for each repository and is not affected.

### Display options

Click the gear icon next to the Dev Hub header to open its settings, where
the **Display** section lets you tailor the layout:

- **Initial keyboard focus** - choose whether keyboard navigation initially
  targets My work or Team activity. Both blades remain visible.
- **Sort issues and pull requests by** - order the lists by **Most recent
  first** (the default), **GitHub first**, or **Azure DevOps first**. The
  provider options group items by source, then fall back to most recent
  within each group. Team requests remain above general repository activity in
  inventory views; Attention always uses newest-updated-first ordering.

### Hiding the Dev Hub

If you don't use the Dev Hub, you can hide it. Click the settings (gear)
button in the top-right of the Start Screen and uncheck **Show Dev Hub**.
The News section expands to use the freed-up space. Re-check the box at
any time to bring it back.

## Refresh, service health, and VS updates

The header groups Refresh with its update timestamp. This single Refresh button
updates repository activity, blog feeds, and videos; news tabs have no separate
refresh link or timestamp. Visual Studio service
health is shown below the action buttons and links to the public status page.
It remains hidden until the first check completes, then stays visible during
refreshes. Unchanged status values and news/video cards are retained without
rebuilding their UI; only changed content is updated.
Unknown, stale, and failed checks are not displayed as healthy. Public service
health does not describe your account sign-in or repository CI status.

The work pane is capped at 560 pixels including its gutter. Extra screen width
goes to news and videos, which expand into multiple columns. At narrow widths,
news moves below the work pane.

A discreet update link uses Visual Studio's setup API and shows the available
version when supplied by VS. It opens the standard Visual Studio update flow;
there is no separate release-notes link. Tips and suggested-extension panels
have been removed to give workspace activity and news more room.

## Keyboard-first design

Every part of Start Screen is navigable without a mouse.

Open the Start Screen any time from **File > Start Screen** or press
**Ctrl+Shift+Alt+Backspace**.

### Recent solutions / folders

| Shortcut   | Action                 |
| ---------- | ---------------------- |
| Up / Down  | Select and preview     |
| Enter      | Open                   |
| Ctrl+Enter | Open in new instance   |
| O          | Open containing folder |
| T          | Open in terminal       |
| G          | Git pull               |
| Ctrl+C     | Copy path              |
| P          | Pin / Unpin            |
| Del        | Remove from list       |
| Alt+`      | Focus search box       |
| Right      | Jump to Dev Hub        |

### Dev Hub

| Shortcut  | Action               |
| --------- | -------------------- |
| Up / Down | Move between items   |
| Enter     | Review PR in VS; other items in browser |
| Ctrl+C    | Copy URL             |
| Left      | Jump to recent files |
| Right     | Jump to news feed    |

### News feed

| Shortcut            | Action                 |
| ------------------- | ---------------------- |
| Up / Down           | Move between rows      |
| Left / Right        | Move between columns   |
| Enter               | Open in browser        |
| Ctrl+C              | Copy URL               |
| Left (first column) | Jump to Dev Hub        |
| Right (last column) | Jump to YouTube videos |

### YouTube videos

| Shortcut  | Action             |
| --------- | ------------------ |
| Up / Down | Move between items |
| Enter     | Open in browser    |
| Ctrl+C    | Copy URL           |
| Left      | Jump to news feed  |

### Action bar

| Shortcut     | Action                     |
| ------------ | -------------------------- |
| Left / Right | Move between buttons       |
| Down         | Move focus to recent files |

## Looks right at home

Start Screen inherits your Visual Studio theme - Light, Dark, Blue, or
whatever you're running. No jarring color mismatches, no extra
configuration. It just blends in.

## Install it

1. Grab it from the [Visual Studio Marketplace][marketplace].
2. Restart Visual Studio.
3. That's it. Start Screen is already waiting for you.

It works out of the box. Feed preferences and pinned items are persisted
automatically.

## Get involved

Found a bug? Have an idea? Head to the [issue tracker][repo] - pull
requests are always welcome.

### Running tests

Run the complete test project in Test Explorer; the project automatically selects
its shared runsettings file. Classes remain parallelized, and normal runs skip
coverage instrumentation; use Analyze Code Coverage or `--collect "Code Coverage"`
when coverage is needed. WPF tests run in the
test-host process without a separate .NET Framework AppDomain, avoiding dispatcher
shutdown deadlocks when the full suite finishes. Diagnostics buffer until the
Visual Studio Output pane is initialized and do not require a running VS host.
CI uses the same settings, stops an inactive test run after two minutes, and
uploads the test results and hang sequence without collecting memory dumps.
