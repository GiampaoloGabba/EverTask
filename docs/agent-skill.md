---
layout: default
title: Agent Skill
nav_order: 90
---

# AI-assisted integration (agent skill)

EverTask ships an **agent skill** that walks an AI coding agent through integrating EverTask into
*your* project: it picks the right packages, wires `AddEverTask(...)` with the options you actually
need, and scaffolds task records + handlers that already satisfy the System.Text.Json payload
analyzers (ET0001–ET0007). It asks you the decisions it cannot infer (storage backend, scheduling,
retry, rate limiting, monitoring) and reads only the reference material for the features you choose.

The skill itself is plain `SKILL.md` markdown (the Agent Skills convention) plus reference files —
**portable across agents**: any AI coding agent that supports the convention, or that you can point
at a folder of instructions, can use it. The **one-step marketplace install below is specific to
[Claude Code](https://claude.com/claude-code)** (the `/plugin` system and marketplaces are a Claude
Code feature); for other agents, see [Other agents](#other-agents). Note NuGet is never involved —
agents discover skills from their skills directory or (on Claude Code) from installed plugins, never
from the NuGet package cache.

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

Once installed, you usually don't need to invoke it explicitly — describe what you want and the
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
queues, monitoring & logging, and the payload contract — with copy-paste code templates. It is
read-oriented and analyzer-aware: the payload checklist matches the Roslyn rules bundled in
`EverTask.Abstractions`, so scaffolded tasks compile clean under warnings-as-errors.
