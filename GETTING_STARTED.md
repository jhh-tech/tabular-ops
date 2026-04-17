# Starting with Claude Code

## Setup

1. Create a new directory for the project somewhere sensible:
   ```bash
   mkdir tabular-ops && cd tabular-ops
   ```

2. Copy the handoff materials into it:
   ```
   tabular-ops/
   ├── CLAUDE.md                      ← project context
   └── docs/
       ├── ui-design.md
       ├── decisions.md
       └── ui-prototype/
           └── tabular-ops-console.html
   ```

3. Initialize git BEFORE starting Claude Code — you want a clean history from the start:
   ```bash
   git init
   git add .
   git commit -m "Project brief and UI prototype from design session"
   ```

4. Launch Claude Code from the project root:
   ```bash
   claude
   ```

Claude Code reads `CLAUDE.md` automatically, so it starts with full project context.

## First prompt suggestions

### Option A — scaffold the solution
If you want to just get the repo structure in place:

> Please read CLAUDE.md and docs/decisions.md. Then scaffold the .NET solution as described in CLAUDE.md under "Project layout". Use .NET 8, WPF for the desktop project, and add the NuGet packages listed in docs/decisions.md. Don't implement any logic yet — just create the project structure with empty/stub classes matching the Core folder hierarchy. Stop after the solution builds successfully.

### Option B — start with Milestone 1
If you want to dive into real code:

> Please read CLAUDE.md and docs/decisions.md. Let's implement Milestone 1 (Connect and browse). Start by creating the solution scaffold, then implement ConnectionManager with MSAL interactive login and token cache. I want to be able to run a simple console test that connects to a Power BI workspace and lists the models. We'll wire up the WPF UI once the Core connection works.

### Option C — review and refine
If you want to iterate on the plan first:

> Please read CLAUDE.md, docs/ui-design.md, and docs/decisions.md. Before we start coding, review the plan and tell me:
> 1. Any architectural concerns you see
> 2. Open questions in decisions.md you have opinions on
> 3. Anything missing from the design that will bite us later
> Then we'll address your concerns and start with Milestone 1.

**Recommended: Option C first, then Option B.** Always better to surface disagreements before writing 10 files of scaffolding.

## Working style tips for this project

- **Commit after each milestone.** The MVP path in CLAUDE.md is structured so each milestone is self-contained. Don't let 4 weeks of work sit uncommitted.
- **Run against a real tenant early.** Power BI XMLA has quirks that only show up with real endpoints. Get your devtest Fabric workspace connection string ready on day one.
- **Don't let Claude Code over-engineer Milestone 1.** You don't need a full plugin architecture, dependency injection container, and logger abstraction before you can list databases. Start simple.
- **Keep the UI prototype open in a browser tab.** It's the visual source of truth. When implementing a view, have the mockup visible for reference.
- **Push back on drift.** If Claude Code starts suggesting features outside the MVP scope (BPA, DAX editor, CI/CD), redirect it. That's what TE3 is for.

## Using the UI prototype in Claude Code

The HTML prototype in `docs/ui-prototype/` is reference material, not code to port. It exists so you can:
- Show Claude Code exact visual behavior ("make it look like the partition grid at line X of tabular-ops-console.html")
- Verify your implementation matches the intent
- Onboard future contributors

When implementing a view, you can reference specific parts: "Implement PartitionMapView.xaml matching the `.partition-grid` section in docs/ui-prototype/tabular-ops-console.html. The selection tray is the `.selection-tray` element."

## When stuck

Good patterns for asking Claude Code for help on this project:

- "Show me how AsPartitionProcessing handles retry logic, then adapt that pattern to our RefreshEngine"
- "The XMLA trace subscription for Power BI isn't returning ProgressReportCurrent events. What events does the Power BI endpoint actually support?"
- "Write an xUnit test that mocks Server and verifies PartitionProcessor correctly batches refreshes"

Bad patterns:

- "Build the whole app" (too broad — work milestone by milestone)
- "Make it better" (specify what better means)
- "Why doesn't this work?" without logs/errors (always paste the exception)

## Milestone exit checklist

Before moving from one milestone to the next, verify:
- [ ] All code compiles with warnings treated as errors
- [ ] A manual test against a real tenant passes
- [ ] Unit tests exist for Core logic that doesn't need a live server
- [ ] `git commit` with a clear milestone marker

Good luck. This is a real tool that will save you time once built — don't let perfect be the enemy of shipped.
