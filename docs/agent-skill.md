---
layout: default
title: Agent Skill
nav_order: 90
---

# AI-assisted integration (agent skill)

EverTask ships an agent skill that integrates the library into your project: it picks the packages, wires `AddEverTask(...)`, and scaffolds tasks and handlers that pass the payload analyzers (ET0001–ET0007). It asks you only the decisions it can't infer (storage, scheduling, retry, rate limiting, monitoring).

Install it on [Claude Code](https://claude.com/claude-code) with the one step below, or point any other agent at the folder (see [Other agents](#other-agents)).

## Install on Claude Code (recommended)

Prerequisite: [Claude Code](https://claude.com/claude-code) installed (`claude` CLI, v2.1+). In a
session, run:

```text
/plugin marketplace add GiampaoloGabba/EverTask
/plugin install evertask@evertask
```

Then activate it immediately with `/reload-plugins` (or just start a new session). The skill is
now available, namespaced as `/evertask:integrate-evertask`.

`/plugin marketplace add GiampaoloGabba/EverTask` registers the catalog at
`.claude-plugin/marketplace.json` in this repo; `/plugin install evertask@evertask` installs the
`evertask` plugin (which carries the skill) from the `evertask` marketplace.

## Use it

Once installed, you usually don't need to invoke it explicitly: describe what you want and the
agent triggers the skill automatically:

> "Add EverTask to this API with PostgreSQL storage and a nightly cleanup job."

Or invoke it directly:

```text
/evertask:integrate-evertask
```

The skill then:

1. Inspects your project (host type, target framework, existing DB / `AddEverTask`).
2. Asks only the open questions (storage, capabilities, throughput profile).
3. Adds the packages and wires `AddEverTask(...)` (proposing the diff first).
4. Scaffolds a task + handler that pass the payload analyzers.
5. Wires the chosen capabilities (scheduling, retry, rate limiting, monitoring, Serilog).
6. Builds to verify.

## Other agents

The skill is just markdown, so it isn't tied to Claude Code. Use either:

- **Manual copy:** copy `plugins/evertask/skills/integrate-evertask/` into wherever your agent looks
  for skills/instructions. On Claude Code that's `~/.claude/skills/` (personal) or a project's
  `.claude/skills/`; other tools use their own location. Auto-discovery and auto-triggering then
  depend on the agent.
- **Point at it directly:** if your agent accepts a skill/instruction directory, hand it the path
  to that folder. As a fallback, any agent can simply read `SKILL.md` and follow it.

On Claude Code you can also **try it without installing**, from a local clone of this repo:

```text
claude --plugin-dir ./plugins/evertask
```

## Update / uninstall

```text
/plugin update evertask@evertask
/plugin uninstall evertask@evertask
```

## What's inside

The skill is a router (`SKILL.md`) plus focused, on-demand reference files mirroring the
documentation: setup & DI, tasks & handlers, storage, resilience, scheduling, rate-limiting &
queues, monitoring & logging, and the payload contract, with copy-paste code templates. It is
read-oriented and analyzer-aware: the payload checklist matches the Roslyn rules bundled in
`EverTask.Abstractions`, so scaffolded tasks compile clean under warnings-as-errors.
