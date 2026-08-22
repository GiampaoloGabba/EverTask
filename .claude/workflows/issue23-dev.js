export const meta = {
  name: 'issue23-dev',
  description: 'Autonomous 7-phase development of issue #23 (durable occurrences) with per-phase gates, adversarial-light reviews, dual-lens certification and a final full adversarial review',
  whenToUse: 'Launch once from the EverTask repo to develop #23 end-to-end (phases #24-#30). Fully autonomous; resumable via args.startPhase or resumeFromRunId.',
  phases: [
    { title: 'Setup' },
    { title: 'Fase 1', detail: 'invarianti: evaluator, clock, compatibilita, migrazione, storage ops, recovery X3' },
    { title: 'Fase 2', detail: 'execution context' },
    { title: 'Fase 3', detail: 'time zones + DST' },
    { title: 'Fase 4', detail: 'occorrenze durevoli + misfire' },
    { title: 'Fase 5', detail: 'ITaskScheduleManager' },
    { title: 'Fase 6', detail: 'INextOccurrenceProvider' },
    { title: 'Fase 7', detail: 'monitoring, UI, docs, release' },
    { title: 'Final review', detail: 'full adversarial review + fix loop' },
  ],
}

// ============================== CONFIG ==============================

const ROOT  = (args && args.repoRoot) || 'E:/Archivio/Sviluppo/Web/EverTask'
const START = (args && args.startPhase) || 1
const END   = (args && args.endPhase) || 7
const RUN_FINAL = !(args && args.skipFinalReview)
const OUT   = ROOT + '/review/orchestrator'

const MAX_REVIEW_ROUNDS = 3   // per-phase review/fix rounds
const MAX_GATE_FIXES    = 3   // build/test fix attempts per gate failure
const MAX_CERT_CYCLES   = 2   // certification -> complete-the-gaps cycles
const MAX_ESCALATIONS   = 2   // last-resort dev attempts at max effort when a phase is stuck
const MAX_FINAL_ROUNDS  = 3   // final review fix rounds

// Model policy (maintainer, 2026-08-22): executor = dev-executor@opus (xhigh on high-risk phases 1/4/6, high otherwise); important finders = fable;
// minor finders = opus; verify/certify/synthesis pinned opus max (never inherit the session model);
// codex = gpt-5.6-sol, xhigh for important runs, high for minor; mechanical runners = sonnet.
const DEV   = { agentType: 'dev-executor', model: 'opus', effort: 'high' }   // normal-risk phases
const DEVX  = { agentType: 'dev-executor', model: 'opus', effort: 'xhigh' }  // high-risk phases (1, 4, 6)
const DEVMAX= { agentType: 'dev-executor', model: 'opus', effort: 'max' }    // escalations and final-review fixes
const devOf = (ph) => (ph && ph.risk === 'high') ? DEVX : DEV
const GATE  = { model: 'opus', effort: 'medium' }
const FABLE = { model: 'fable', effort: 'high' }
const MINOR = { model: 'opus', effort: 'high' }
const JUDGE = { model: 'opus', effort: 'max' }
const RUNNER= { model: 'sonnet' }

// ============================== SCHEMAS ==============================

const DEV_SCHEMA = { type: 'object', required: ['done', 'summary'], properties: {
  done: { type: 'boolean' }, summary: { type: 'string' },
  filesTouched: { type: 'array', items: { type: 'string' } },
  blockers: { type: 'array', items: { type: 'string' } },
  notes: { type: 'array', items: { type: 'string' } } } }

const GATE_SCHEMA = { type: 'object', required: ['ok', 'summary'], properties: {
  ok: { type: 'boolean' }, summary: { type: 'string' },
  failures: { type: 'array', items: { type: 'string' } } } }

const FINDINGS_SCHEMA = { type: 'object', required: ['findings'], properties: {
  coverageNotes: { type: 'string' },
  findings: { type: 'array', items: { type: 'object',
    required: ['severity', 'title', 'file', 'claim', 'scenario'], properties: {
      severity: { type: 'string', enum: ['critical', 'high', 'medium', 'low'] },
      title: { type: 'string' }, file: { type: 'string' }, line: { type: 'number' },
      claim: { type: 'string' }, scenario: { type: 'string' } } } } } }

const CODEX_SCHEMA = { type: 'object', required: ['status', 'findings'], properties: {
  status: { type: 'string', enum: ['done', 'error', 'suspicious'] },
  errorDetail: { type: 'string' },
  findings: FINDINGS_SCHEMA.properties.findings } }

const VERDICT_SCHEMA = { type: 'object', required: ['refuted', 'reason'], properties: {
  refuted: { type: 'boolean' }, reason: { type: 'string' } } }

const CERT_SCHEMA = { type: 'object', required: ['complete', 'missing'], properties: {
  complete: { type: 'boolean' },
  missing: { type: 'array', items: { type: 'string' } },
  notes: { type: 'string' } } }

const CODEX_CERT_SCHEMA = { type: 'object', required: ['status', 'complete', 'missing'], properties: {
  status: { type: 'string', enum: ['done', 'error', 'suspicious'] },
  errorDetail: { type: 'string' },
  complete: { type: 'boolean' },
  missing: { type: 'array', items: { type: 'string' } } } }

// ============================== PHASE TABLE ==============================

const PHASES = [
  { n: 1, issue: 24, planSection: '## 1. FASE 1', risk: 'high', perf: true,
    title: 'Invarianti: evaluator, clock deterministico, compatibilita, migrazione unica, operazioni storage atomiche, recovery X3',
    fableLenses: [
      'storage atomicity: MaterializeOccurrence/TrySetRecurringSeriesCompleted/TryRequeueStaleOccurrence/TryHaltSchedule/CancelSchedule CAS semantics, unique index + restrict FK + check constraint, per-provider parity (SqlServer procs, Postgres CTEs, MySQL procs, SQLite, Memory), migration Up/Down correctness',
      'backward compatibility: golden-byte JSON of legacy schedules, JsonIgnore placement, init-only record extensions, ToLazy via with, consumer baseline fixture (compiled against issue23-baseline packages, NOT the 3.11 nupkg), DIM delegation direction for nowUtc overloads (custom storages must not be bypassed)',
      'recovery correctness: X3 grouped predicate identical in all 4 copies and SQL-translatable, execution vs finalize-only split, finalize-before-grace ordering, natural-successor grace, TimeProvider threading through dispatcher/worker/schedulers/builders/limiter with no behavior change' ],
    minorLens: 'code quality: reuse, simplification, allocation/round-trip regressions on hot paths, naming consistency with the existing codebase' },
  { n: 2, issue: 25, planSection: '## 2. FASE 2', risk: 'normal', perf: false,
    title: 'Execution context (ITaskExecutionContext)',
    fableLenses: [
      'context correctness: eager vs lazy handler scopes (AsyncLocal ambient accessor), injection before OnStarted via cached typed delegates, Attempt semantics across retries and callbacks, nominal slot immutability vs rate-limit reserved slot, RunNumber durability across restarts' ],
    minorLens: 'API shape and docs: DIM non-breaking, Context throwing getter, cheatsheet/reference/skill updated in the same change' },
  { n: 3, issue: 26, planSection: '## 3. FASE 3', risk: 'normal', perf: false,
    title: 'Time zones con semantica DST',
    fableLenses: [
      'DST math: WallClock.ToUtc gap/overlap/Consumed/collapse, Elapsed vs Calendar classification (EveryHour().AtMinute(30) is Elapsed), Cronos oracle equivalence from the 2nd occurrence, IsUniformGrid by semantics, IANA normalization, half-hour zones (Lord Howe, Kolkata, Kathmandu, Chatham)' ],
    minorLens: 'builder API, docs corrections (best-practices frozen-offset example, task-dispatching BaseUtcOffset), new time-zones.md quality' },
  { n: 4, issue: 27, planSection: '## 4. FASE 4', risk: 'high', perf: true,
    title: 'Occorrenze durevoli + misfire policies',
    fableLenses: [
      'materializer safety: per-parent lock + global budget, kick non-throwing before the single deliveryRegistry.End, reconciliation vs in-flight deliveries (never reschedule a delivering child), stale requeue CAS, two-pass recovery barrier, cancel/halt races, schedule-only executor bypassing the rate-limit gate without burning budget',
      'caps and policies: MaxAge as only ordinary loss with events, MaxOccurrences bounded counting with DetectedAtLeast, durable Halt persisted and never self-releasing, SkipOldest bisection correctness, MaxPendingOccurrences window incl. window-full liveness re-park, MaxRuns counts materializations, RunUntil exclusive, BackfillFrom',
      'end-to-end integrity: child rows always slot-NOT-NULL, unique-violation mapped to AlreadyExists by constraint name on every provider, at-least-once documented, multi-host materialization idempotence, retention of terminal occurrences, cascade CancelSchedule atomicity' ],
    minorLens: 'code quality, event payloads, docs (durable-occurrences.md, scalability.md rewrite), integration-first test realness (no rigged mocks)' },
  { n: 5, issue: 28, planSection: '## 5. FASE 5', risk: 'normal', perf: false,
    title: 'ITaskScheduleManager',
    fableLenses: [
      'reschedule correctness: CAS advance vs completion race, RebaseFromCursor nominal-period mapping incl. zone change (Rome->Kiritimati keeps the logical day), shape-compatibility rejections, ScheduleVersionRegistry semantics (absence = no lower bound, publish only after successful latest-wins re-park, removal at series end), InProgress never rejected, Halt release via ResumeSchedule, RequeueFailedOccurrence keeps id+audit' ],
    minorLens: 'API/docs quality, NotSupported behavior on storages without versioning' },
  { n: 6, issue: 29, planSection: '## 6. FASE 6', risk: 'high', perf: false,
    title: 'INextOccurrenceProvider',
    fableLenses: [
      'failure classification: transient provider exception re-parks with backoff and NEVER reaches the recovery poison counter (L18) nor marks Failed; unknown key = dispatch exception / recovery poison; non-monotonic provider result handling',
      'evaluator integration: bounded counting through providers, SkipOldest requires IsDeterministic, RebaseFromCursor rejected, provider + durable CatchUp + skip-forward after downtime, scoped resolution and cancellation',
      'contract quality: NextOccurrenceRequest fields, strictly-after guarantee, null ends series, opaque config, registry key validation on all paths (dispatch, recovery, reschedule)' ],
    minorLens: 'API/docs, sample BusinessDaysProvider realism' },
  { n: 7, issue: 30, planSection: '## 7. FASE 7', risk: 'normal', perf: false,
    title: 'Monitoring API/UI, docs sweep, samples, release 4.0.0',
    fableLenses: [
      'surface coherence: DTOs vs mirrored TS types (must match exactly), new events wired in BOTH EverTaskEventData constructors, occurrence endpoints and backlog-by-state semantics, monitoring-events.md accuracy (it was already stale), pnpm build + wwwroot embedding' ],
    minorLens: 'docs sweep completeness: README blurb, index, config reference ToC, CHANGELOG, version bump, CLAUDE.md locals, integrate-evertask skill wizard, new-relational-storage-provider skill in .claude AND .agents' },
]

// ============================== PROMPT HELPERS ==============================

const SPEC = 'Read FIRST, in full: ' + ROOT + '/review/recurring-occurrences-decisions.md (the decisions file: it PREVAILS over any other default) and ' + ROOT + '/review/recurring-occurrences-plan.md (the implementation plan). '

const HOUSE_RULES = [
  'House rules (binding):',
  '- Repo root: ' + ROOT + '. Build: dotnet build EverTask.slnx -c Release (warnings are errors). Tests: dotnet test EverTask.slnx -c Release (full suite INCLUDING Testcontainers; Docker is available - verify with docker info; NEVER prune docker resources).',
  '- Tests are integration-first with REAL parts (real IHost, real storage, Testcontainers). Unit tests only where they genuinely fit (pure math, enumerators, DST). NEVER write tests rigged to pass or mocks that hide real behavior.',
  '- Performance parity from the first commit: new storage operations implemented at each provider\'s optimization tier (SqlServer stored procedures, Postgres writable CTEs, MySQL stored procedures, EF Core base single SaveChanges). No extra round-trips on existing hot paths.',
  '- The legacy default path must stay byte-identical: existing test suites must pass UNCHANGED (only allowed edits: positional-record arity in the two pinned construction tests, plus the X3 recovery-filter tests explicitly described in the plan).',
  '- Every public option added must update, in the same change: docs/configuration-cheatsheet.md, docs/configuration-reference.md, plugins/evertask/skills/integrate-evertask/. Storage-surface changes must also update .claude/skills/new-relational-storage-provider/ AND its mirror .agents/skills/new-relational-storage-provider/.',
  '- Any file under docs/ that you create or modify must be passed through the humanizer skill (invoke the Skill tool with skill "humanizer"). If the Skill tool or the skill is unavailable in your session, say so explicitly in your final notes - do not silently skip.',
  '- C# style: primary constructors where applicable; 4-space indent; follow .editorconfig and the local CLAUDE.md files of every module you touch.',
  '- Do NOT commit, push, tag or run destructive git commands. The orchestrator commits at phase end.',
  '- Use pnpm (never npm) for the monitoring UI.',
].join('\n')

const CODEX_CMD = (dir, effort) =>
  'codex exec -C "' + ROOT + '" --skip-git-repo-check -s read-only -c approval_policy=never -c model_reasoning_effort=' + effort +
  ' --json -o "' + dir + '/last-message.md" - < "' + dir + '/prompt.md" > "' + dir + '/events.jsonl" 2> "' + dir + '/stderr.log"'

function codexRunnerPrompt(dir, effort, reviewBrief) {
  return [
    'You are a mechanical runner. Execute EXACTLY these steps, no improvisation, no flag changes, no retries.',
    '1. mkdir -p "' + dir + '" (bash).',
    '2. Write the file "' + dir + '/prompt.md" with EXACTLY this content between the BEGIN/END markers (do not include the markers):',
    'BEGIN', reviewBrief, 'END',
    '3. Run with the Bash tool, timeout 600000 ms, this exact command:',
    CODEX_CMD(dir, effort),
    '4. Classify: exit 0 + non-empty last-message.md + a final {"type":"turn.completed"} line in events.jsonl => done; exit != 0 or turn.failed => error (grep the last error item in events.jsonl and put it in errorDetail); exit 0 but empty/missing last-message.md => suspicious.',
    '5. If done: read last-message.md and extract every finding into the schema (severity mapped to critical/high/medium/low; file paths repo-relative). If the message contains no findings, return an empty findings array with status done.',
    '6. Return ONLY via the structured output schema. Never paste raw codex output.',
  ].join('\n')
}

function codexReviewBrief(scope, extra) {
  return [
    'Sei un revisore avversariale senior di librerie .NET di scheduling durevole. SOLO revisione statica: non modificare file, non eseguire build/test.',
    'Leggi per intero review/recurring-occurrences-decisions.md (prevale) e review/recurring-occurrences-plan.md, poi analizza il codice indicato.',
    'Scope: ' + scope,
    extra || '',
    'Cerca SOLO difetti reali e azionabili: correttezza, race, perdita dati, doppia esecuzione, violazioni della spec, regressioni del path legacy, violazioni di performance (round-trip aggiunti, allocazioni sui hot path), test finti o non significativi.',
    'Per ogni finding: severita (critical/high/medium/low), file:riga, claim di una frase, scenario concreto input->esito sbagliato. Niente stile, niente nitpick, niente complimenti. Se non trovi nulla di sostanziale, dillo esplicitamente.',
  ].join('\n')
}

function findingsList(items) {
  return items.map((f, i) => (i + 1) + '. [' + f.severity + '] ' + f.title + ' @ ' + f.file + (f.line ? (':' + f.line) : '') + ' - ' + f.claim + ' | scenario: ' + f.scenario).join('\n')
}

function fkey(f) {
  return (f.file || '') + '|' + (f.title || '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim().slice(0, 80)
}

// ============================== BUILDING BLOCKS ==============================

async function runGates(phaseTag, focus) {
  return await agent([
    'Verification gate for the EverTask repo at ' + ROOT + '. Run, in order, with the Bash tool (long timeouts):',
    '1. docker info (must succeed; if not, report ok=false with failure "docker down" - do NOT try to fix Docker).',
    '2. cd "' + ROOT + '" && dotnet build EverTask.slnx -c Release  (must end with 0 warnings / 0 errors).',
    '3. cd "' + ROOT + '" && dotnet test EverTask.slnx -c Release  (FULL suite, Testcontainers included; use a 600000 ms Bash timeout and run_in_background+wait if needed; if the runner splits by TFM let it).',
    focus ? ('Extra focus: ' + focus) : '',
    'Return ok=true only if build has zero warnings AND every test passed. On failure list each failing test/compile error verbatim (trimmed) in failures. Never mark ok=true with any failure present. Do not modify any file.',
  ].join('\n'), { ...GATE, label: 'gate:' + phaseTag, phase: phaseTag, schema: GATE_SCHEMA })
}

async function devFixLoop(phaseTag, phaseObj, gateResult) {
  let gates = gateResult
  for (let i = 1; !((gates && gates.ok)) && i <= MAX_GATE_FIXES; i++) {
    log('Fase ' + phaseObj.n + ': gate rosso, fix attempt ' + i)
    await agent([
      SPEC,
      'You are fixing build/test failures introduced while implementing phase ' + phaseObj.n + ' (' + phaseObj.title + ') of the plan (' + phaseObj.planSection + ').',
      HOUSE_RULES,
      'Current failures:\n' + ((gates && gates.failures) || []).join('\n'),
      'Diagnose the root cause and fix it properly (never weaken or delete a legacy test to make it pass; if a NEW test is wrong, fix the test only when the spec says the implementation is right). Run the relevant build/tests yourself to confirm before finishing.',
    ].join('\n'), { ...devOf(phaseObj), label: 'fix:gates:' + i, phase: phaseTag, schema: DEV_SCHEMA })
    gates = await runGates(phaseTag, 'previously failing: ' + ((gates && gates.failures) || []).slice(0, 10).join(' ; '))
  }
  return gates
}

async function verifyFinding(f, phaseTag, skeptics) {
  const votes = await parallel(Array.from({ length: skeptics }, (_, k) => () =>
    agent([
      'Adversarial skeptic. Your job is to REFUTE this code-review finding against the ACTUAL code in ' + ROOT + '. Default to refuted=true if you cannot reproduce the reasoning from the real code. Read the cited file and every relevant caller; check the spec (review/recurring-occurrences-decisions.md) if the finding claims a spec violation.',
      'Finding: [' + f.severity + '] ' + f.title,
      'File: ' + f.file + (f.line ? (':' + f.line) : ''),
      'Claim: ' + f.claim,
      'Scenario: ' + f.scenario,
      'refuted=true means: not a real defect (wrong reading, already handled, spec-compliant, cannot happen). refuted=false means the defect is real and material.',
    ].join('\n'), { ...JUDGE, label: 'verify:' + (f.file || '?') + ':' + k, phase: phaseTag, schema: VERDICT_SCHEMA })))
  const valid = votes.filter(Boolean)
  if (valid.length === 0) return { confirmed: false, reason: 'no skeptic result' }
  const refutes = valid.filter(v => v.refuted).length
  // killed when at least half of the skeptics refute (refute-by-default floor)
  return { confirmed: refutes * 2 < valid.length, reason: valid.map(v => (v.refuted ? 'REFUTED: ' : 'STANDS: ') + v.reason).join(' || ') }
}

async function reviewRound(phaseObj, phaseTag, round, seen, focusNote) {
  const isHigh = phaseObj.risk === 'high'
  const lenses = round === 1 ? phaseObj.fableLenses : [phaseObj.fableLenses[0]]
  const scope = 'phase ' + phaseObj.n + ' (' + phaseObj.title + ') - review the UNCOMMITTED working-tree changes: run git status and git diff HEAD (and read new files in full), then read every touched file with its surroundings.'

  const finderJobs = []
  for (const lens of lenses) {
    finderJobs.push(() => agent([
      SPEC, 'Adversarial finder. ' + scope, 'Your single lens: ' + lens,
      focusNote || '',
      'Also verify the phase respects: legacy byte-identical, integration-first real tests, per-provider optimization tier, spec conformance (decisions prevail). Report ONLY defects with concrete failure scenarios (severity critical/high/medium/low, repo-relative file, line, one-sentence claim, scenario). No style nits. Read code, never guess.',
    ].join('\n'), { ...FABLE, label: 'find:fable:' + round, phase: phaseTag, schema: FINDINGS_SCHEMA }))
  }
  if (round === 1) {
    finderJobs.push(() => agent([
      SPEC, 'Adversarial finder. ' + scope, 'Your single lens: ' + phaseObj.minorLens,
      'Report ONLY actionable issues (schema severities; medium max unless something is truly broken). No style nits.',
    ].join('\n'), { ...MINOR, label: 'find:minor:' + round, phase: phaseTag, schema: FINDINGS_SCHEMA }))
  }
  finderJobs.push(() => agent(
    codexRunnerPrompt(OUT + '/codex/f' + phaseObj.n + '-r' + round, isHigh ? 'xhigh' : 'high',
      codexReviewBrief('fase ' + phaseObj.n + ' (' + phaseObj.title + '): modifiche non committate nel working tree (git status / git diff HEAD) rispetto alla sezione ' + phaseObj.planSection + ' del piano.', focusNote)),
    { ...RUNNER, label: 'find:codex:' + round, phase: phaseTag, schema: CODEX_SCHEMA }))

  const results = (await parallel(finderJobs)).filter(Boolean)
  const codexRes = results.find(r => r && r.status)
  if (codexRes && codexRes.status !== 'done') log('Fase ' + phaseObj.n + ' round ' + round + ': codex ' + codexRes.status + ' (' + (codexRes.errorDetail || 'n/a') + ') - coverage ridotta a soli finder Claude, registrato nel report')

  const all = results.flatMap(r => (r.findings || []))
  const fresh = all.filter(f => !seen.has(fkey(f)))
  fresh.forEach(f => seen.add(fkey(f)))
  const candidates = fresh.filter(f => f.severity !== 'low')
  const lows = fresh.filter(f => f.severity === 'low')

  const verified = []
  for (const f of candidates) {
    const v = await verifyFinding(f, phaseTag, 1)
    if (v.confirmed) verified.push({ ...f, verdict: v.reason })
  }
  return { confirmed: verified, lows, codexStatus: codexRes ? codexRes.status : 'missing' }
}

async function certify(phaseObj, phaseTag) {
  const certPrompt = [
    SPEC,
    'Completeness certifier for phase ' + phaseObj.n + ' (' + phaseObj.title + '). Compare the UNCOMMITTED working-tree changes (git status / git diff HEAD, read new files fully) against EVERY deliverable of section "' + phaseObj.planSection + '" of the plan and every decision it references, including: tests listed for the phase (present AND meaningful, integration-first), docs/cheatsheet/reference/skill updates required in the same phase, per-provider storage parity, and the gates of section 0 of the plan.',
    'complete=true ONLY if nothing required by the plan section is missing or half-done. List every gap in missing (one precise line each, with the plan bullet it comes from). Read code and tests, never assume.',
  ].join('\n')
  const [fable, codex] = await parallel([
    () => agent(certPrompt, { ...FABLE, effort: 'max', label: 'certify:fable', phase: phaseTag, schema: CERT_SCHEMA }),
    () => agent(codexRunnerPrompt(OUT + '/codex/cert-f' + phaseObj.n, phaseObj.risk === 'high' ? 'xhigh' : 'high', [
      'Sei un certificatore di completezza. SOLO lettura, nessuna modifica, nessun build/test.',
      'Confronta le modifiche non committate del working tree (git status / git diff HEAD) con TUTTI i deliverable della sezione "' + phaseObj.planSection + '" di review/recurring-occurrences-plan.md e con le decisioni collegate in review/recurring-occurrences-decisions.md (test inclusi: presenti E significativi).',
      'Rispondi: complete true/false e l\'elenco preciso di ogni mancanza (missing).',
    ].join('\n')), { ...RUNNER, label: 'certify:codex', phase: phaseTag, schema: CODEX_CERT_SCHEMA }),
  ])
  const missing = []
  let complete = true
  if (fable) { if (!fable.complete) complete = false; missing.push(...(fable.missing || []).map(m => 'fable: ' + m)) }
  else { complete = false; missing.push('fable certifier returned no result') }
  if (codex && codex.status === 'done') { if (!codex.complete) complete = false; missing.push(...(codex.missing || []).map(m => 'codex: ' + m)) }
  else { log('Certificazione fase ' + phaseObj.n + ': codex non disponibile (' + (codex ? codex.status : 'null') + ') - vale la sola lente fable, registrato') }
  return { complete, missing }
}

async function commitPhase(phaseObj, phaseTag, reportLines) {
  return await agent([
    'Release clerk for the EverTask repo at ' + ROOT + '. The maintainer has pre-authorized these exact operations for this run. Execute with Bash:',
    '1. cd "' + ROOT + '" && git add -A -- . ":!review/orchestrator" && git status --short  (verify only phase-related files are staged; NEVER stage review/orchestrator).',
    '2. Write a commit message file (temp path) with subject: "feat(scheduler): phase ' + phaseObj.n + ' of #23 - ' + phaseObj.title.toLowerCase().slice(0, 60) + ' (#' + phaseObj.issue + ')" - adjust the conventional scope if the phase is docs/monitoring-heavy - and a body of 3-6 bullet lines summarizing the changes. Commit with git commit -F <file>. Do NOT use -m with multiline text, do NOT push, do NOT amend, do NOT skip hooks.',
    '3. Write the phase report to "' + OUT + '/phase-' + phaseObj.n + '-report.md" (create dirs) with this content:',
    reportLines,
    '4. Post the same report as a comment on GitHub issue #' + phaseObj.issue + ' with: gh issue comment ' + phaseObj.issue + ' --body-file "<report path>", then close it: gh issue close ' + phaseObj.issue + ' --comment "Implemented on the feature/issue23-durable-occurrences branch (merges to master via PR), see the report above."',
    'Return done=true with the commit hash in summary. If the commit hook fails, report done=false with the error in blockers - do not bypass hooks.',
  ].join('\n'), { model: 'opus', effort: 'low', label: 'commit:f' + phaseObj.n, phase: phaseTag, schema: DEV_SCHEMA })
}

// ============================== SETUP ==============================

log('Orchestratore #23: fasi ' + START + '-' + END + (RUN_FINAL ? ' + final review' : '') + ' su ' + ROOT)

if (START === 1) {
  const setup = await agent([
    'Setup for the #23 orchestrator on ' + ROOT + '. The maintainer pre-authorized these operations. With Bash:',
    '1. docker info must succeed (report ok=false otherwise, do not fix).',
    '2. cd "' + ROOT + '" && git status --short - the tree must be COMPLETELY clean (no modified files, no untracked files except review/orchestrator/). If anything is dirty, report ok=false listing it and STOP: the maintainer must clean or commit it before launching. NEVER discard or stash anything yourself.',
    '3. git tag -f issue23-baseline (baseline for the final whole-feature diff).',
    '4. mkdir -p "' + OUT + '/codex". Ensure "review/orchestrator/" is ignored: check .gitignore and append the line "review/orchestrator/" if missing.',
    '5. Capture the perf baseline: read C:/Users/Giampaolo/.claude/projects/E--Archivio-Sviluppo-Web-EverTask/memory/evertask-perf-benchmark-harness.md to learn how to run the LoadHarness, run the standard benchmark, and save the results to "' + OUT + '/perf-baseline.md" (raw numbers + how they were produced). If the harness cannot run, say so in failures but continue (ok stays true).',
    '6. dotnet build EverTask.slnx -c Release must be green before starting.',
    'Return ok plus a summary.',
  ].join('\n'), { ...GATE, label: 'setup', phase: 'Setup', schema: GATE_SCHEMA })
  if (!setup || !setup.ok) return { aborted: 'setup failed', detail: setup }
  log('Setup ok: ' + setup.summary)
}

// ============================== PHASE LOOP ==============================

const phaseSummaries = []

for (const ph of PHASES) {
  if (ph.n < START || ph.n > END) continue
  const TAG = 'Fase ' + ph.n
  log('=== FASE ' + ph.n + ' (#' + ph.issue + '): ' + ph.title)

  // ---- develop
  let dev = await agent([
    SPEC,
    'Implement PHASE ' + ph.n + ' of issue #23 for EverTask, end to end: every deliverable of plan section "' + ph.planSection + '" (files, behaviors, migrations, tests, docs and skill updates listed there). Sub-issue: #' + ph.issue + ' (read it with gh issue view ' + ph.issue + ' for context).',
    HOUSE_RULES,
    'Work TDD and integration-first. Before finishing: run the full build and the complete test suite yourself (Testcontainers included) and make them green. Summarize what you built, list touched files, and list anything you could NOT complete in blockers (empty if none).',
  ].join('\n'), { ...devOf(ph), label: 'dev:f' + ph.n, phase: TAG, schema: DEV_SCHEMA })

  // ---- gates (+ fix loop, + escalation)
  let gates = await runGates(TAG, null)
  gates = await devFixLoop(TAG, ph, gates)
  for (let esc = 1; (!gates || !gates.ok || (dev && dev.blockers && dev.blockers.length)) && esc <= MAX_ESCALATIONS; esc++) {
    log('Fase ' + ph.n + ': escalation ' + esc + ' (dev-executor a effort max)')
    dev = await agent([
      SPEC,
      'ESCALATION attempt ' + esc + ' for phase ' + ph.n + ' (' + ph.planSection + '). A previous developer left the phase incomplete or red. Take full ownership: reread the spec section, inspect the current working tree, finish every deliverable and make the FULL suite green.',
      'Open blockers reported: ' + JSON.stringify((dev && dev.blockers) || []),
      'Gate failures: ' + JSON.stringify((gates && gates.failures) || []).slice(0, 4000),
      HOUSE_RULES,
    ].join('\n'), { ...DEVMAX, label: 'dev:esc' + esc + ':f' + ph.n, phase: TAG, schema: DEV_SCHEMA })
    gates = await runGates(TAG, null)
    gates = await devFixLoop(TAG, ph, gates)
  }
  if (!gates || !gates.ok) {
    phaseSummaries.push({ phase: ph.n, status: 'BLOCKED', detail: (gates && gates.failures) || ['no gate result'] })
    return { aborted: 'fase ' + ph.n + ' bloccata dopo ogni tentativo (gates rossi)', phases: phaseSummaries, gateFailures: gates && gates.failures }
  }

  // ---- review loop (adversarial-light) until diminishing returns
  const seen = new Set()
  const lowsAll = []
  const acceptedMediums = []
  let codexNote = ''
  let lastFixed = []
  for (let round = 1; ; round++) {
    const focus = lastFixed.length === 0 ? null : 'Focused round ' + round + ': the previous round\'s confirmed findings were just fixed - verify the fixes are real and hunt regressions around them. Fixed items:\n' + findingsList(lastFixed)
    const r = await reviewRound(ph, TAG, round, seen, focus)
    lowsAll.push(...r.lows)
    if (r.codexStatus !== 'done') codexNote = 'codex coverage incomplete in round ' + round + ' (' + r.codexStatus + ')'
    log('Fase ' + ph.n + ' review round ' + round + ': ' + r.confirmed.length + ' confermati (medium+), ' + r.lows.length + ' low')
    if (r.confirmed.length === 0) break // diminishing returns: only lows (or nothing) left
    if (round > MAX_REVIEW_ROUNDS) {
      const critical = r.confirmed.filter(f => f.severity === 'critical' || f.severity === 'high')
      if (critical.length) return { aborted: 'fase ' + ph.n + ': finding critical/high ancora confermati dopo ' + MAX_REVIEW_ROUNDS + ' round di fix', open: critical, phases: phaseSummaries }
      acceptedMediums.push(...r.confirmed)
      log('Fase ' + ph.n + ': cap review raggiunto, ' + r.confirmed.length + ' medium residui registrati nel report')
      break
    }
    await agent([
      SPEC,
      'Fix these CONFIRMED review findings of phase ' + ph.n + ' (' + ph.planSection + '), root-cause fixes only (no suppressions, no test weakening). Add or extend REAL tests pinning each fix. Run build + full tests yourself before finishing.',
      findingsList(r.confirmed),
      HOUSE_RULES,
    ].join('\n'), { ...devOf(ph), label: 'fix:review:r' + round, phase: TAG, schema: DEV_SCHEMA })
    gates = await runGates(TAG, 'post-review-fix round ' + round)
    gates = await devFixLoop(TAG, ph, gates)
    if (!gates || !gates.ok) return { aborted: 'fase ' + ph.n + ': gates rossi dopo i fix del review round ' + round, phases: phaseSummaries }
    lastFixed = r.confirmed
  }

  // ---- perf gate (phases 1 and 4)
  let perfNote = 'n/a'
  if (ph.perf) {
    const perf = await agent([
      'Performance gate. Run the LoadHarness benchmark exactly as recorded in "' + OUT + '/perf-baseline.md" (same scenarios), save results to "' + OUT + '/perf-after-phase-' + ph.n + '.md", and compare against the baseline. ok=false only for a MATERIAL regression on dispatch/execute/recurring-advance hot paths (>10% throughput loss or clearly higher per-task allocations); note normal noise as ok. If the baseline is missing, say so and return ok=true with that note.',
    ].join('\n'), { ...GATE, label: 'perf:f' + ph.n, phase: TAG, schema: GATE_SCHEMA })
    perfNote = perf ? perf.summary : 'no result'
    if (perf && !perf.ok) {
      log('Fase ' + ph.n + ': regressione perf - un tentativo di fix')
      await agent([SPEC, 'A material performance regression was measured after phase ' + ph.n + ':\n' + perf.summary + '\nFailures: ' + JSON.stringify(perf.failures || []) + '\nFind and remove the regression without changing behavior (profile if needed). Run build + full tests before finishing.', HOUSE_RULES].join('\n'),
        { ...DEVMAX, label: 'fix:perf', phase: TAG, schema: DEV_SCHEMA })
      gates = await runGates(TAG, 'post perf fix'); gates = await devFixLoop(TAG, ph, gates)
      if (!gates || !gates.ok) return { aborted: 'fase ' + ph.n + ': gates rossi dopo il fix perf', phases: phaseSummaries }
      const perf2 = await agent(['Re-run the LoadHarness comparison against "' + OUT + '/perf-baseline.md", save to "' + OUT + '/perf-after-phase-' + ph.n + '-retry.md". Same ok criteria as before.'].join('\n'), { ...GATE, label: 'perf:retry', phase: TAG, schema: GATE_SCHEMA })
      perfNote = (perf2 ? perf2.summary : 'no retry result') + (perf2 && perf2.ok ? '' : ' [REGRESSION STILL PRESENT - recorded]')
    }
  }

  // ---- certification (dual lens) + gap-filling loop
  let cert = await certify(ph, TAG)
  for (let c = 1; !cert.complete && c <= MAX_CERT_CYCLES; c++) {
    log('Fase ' + ph.n + ': certificazione incompleta (' + cert.missing.length + ' gap), ciclo ' + c)
    await agent([SPEC, 'The completeness certifiers found these gaps in phase ' + ph.n + ' (' + ph.planSection + '). Close EVERY one of them properly (implementation, tests, docs - whatever each gap requires), then run build + full tests.', 'Gaps:\n- ' + cert.missing.join('\n- '), HOUSE_RULES].join('\n'),
      { ...devOf(ph), label: 'fix:cert:' + c, phase: TAG, schema: DEV_SCHEMA })
    gates = await runGates(TAG, 'post certification gap-filling'); gates = await devFixLoop(TAG, ph, gates)
    if (!gates || !gates.ok) return { aborted: 'fase ' + ph.n + ': gates rossi dopo il completamento certificazione', phases: phaseSummaries }
    cert = await certify(ph, TAG)
  }
  if (!cert.complete) return { aborted: 'fase ' + ph.n + ': certificazione ancora incompleta dopo ' + MAX_CERT_CYCLES + ' cicli', missing: cert.missing, phases: phaseSummaries }

  // ---- commit + report + close sub-issue
  const report = [
    '## Phase ' + ph.n + ' report - ' + ph.title,
    'Dev summary: ' + ((dev && dev.summary) || 'n/a'),
    'Gates: green (full suite incl. Testcontainers). Perf: ' + perfNote + '.',
    'Review: ' + seen.size + ' raw findings triaged; confirmed medium+ all fixed and re-verified; ' + lowsAll.length + ' low-severity notes recorded below. ' + codexNote,
    'Certification: complete (Fable + Codex).',
    acceptedMediums.length ? ('OPEN medium findings accepted at review cap (follow-up material):\n' + findingsList(acceptedMediums)) : 'Open medium findings: none.',
    lowsAll.length ? ('Low notes:\n' + findingsList(lowsAll)) : 'Low notes: none.',
  ].join('\n')
  const committed = await commitPhase(ph, TAG, report)
  if (!committed || !committed.done) return { aborted: 'fase ' + ph.n + ': commit fallito', detail: committed, phases: phaseSummaries }
  log('Fase ' + ph.n + ' COMPLETATA e committata: ' + committed.summary)
  phaseSummaries.push({ phase: ph.n, status: 'DONE', commit: committed.summary, perf: perfNote, lows: lowsAll.length })
}

// ============================== FINAL FULL ADVERSARIAL REVIEW ==============================

if (!RUN_FINAL) return { phases: phaseSummaries, finalReview: 'skipped by args' }

log('=== FINAL REVIEW (intera feature, issue23-baseline..HEAD)')
const FTAG = 'Final review'
const FINAL_SCOPE = 'the WHOLE #23 feature: everything in git diff issue23-baseline..HEAD (plus the current tree). Cross-phase interactions matter most: things each per-phase review could not see.'

const FINAL_FABLE_LENSES = [
  'concurrency and double execution: materializer vs kick vs recovery vs cancel vs reschedule interleavings, TaskDeliveryRegistry End discipline, scheduler latest-wins, CAS completeness across every write path',
  'data loss and crash recovery: fault windows (insert vs advance vs schedule vs completion), finalize-vs-cancel, catch-up after downtime within caps, zombie rows, at-least-once documented honestly',
  'time and DST: full sweep of the zone math against Cronos semantics, Elapsed/Calendar boundaries, rebase nominal periods, skip-forward with zones, clock-domain consistency (P9)',
  'storage: per-provider parity of every new op (procs/CTEs vs base), migration Up/Down, SQL translation of new predicates, indexes/constraints, retention, hot-path round-trips and allocations',
  'public surface and compatibility: golden JSON, record extensions, DIM defaults, builder DIMs, consumer baseline fixture, event wire format, API naming coherence, docs accuracy vs behavior',
]
const FINAL_OPUS_LENSES = [
  'test suite honesty: are the new tests REAL (integration-first, real storage/host), do they pin the test matrix of plan section 8, is anything rigged, tautological or asserting too little?',
  'simplification and reuse: duplicated logic across phases, dead seams, needless complexity worth removing before release',
]

async function commitFinalFixes(round, fixed) {
  await agent([
    'Release clerk at ' + ROOT + ' (pre-authorized). Bash: stage everything except review/orchestrator (git add -A -- . ":!review/orchestrator"), commit via message file with subject "fix(scheduler): final adversarial review round ' + round + ' fixes (#23)" and a body listing: ' + fixed.map(f => f.title).join('; ').slice(0, 1500) + '. No push, no amend, no hook skipping.',
  ].join('\n'), { model: 'opus', effort: 'low', label: 'commit:final:r' + round, phase: FTAG, schema: DEV_SCHEMA })
}

const seenF = new Set()
const finalOpenMediums = []
let finalLastFixed = []
for (let round = 1; ; round++) {
  const focus = finalLastFixed.length === 0 ? '' : 'Focused round after fixes. Just-fixed items to re-verify and hunt regressions around:\n' + findingsList(finalLastFixed)
  const jobs = []
  const lensSet = round === 1 ? FINAL_FABLE_LENSES : FINAL_FABLE_LENSES.slice(0, 2)
  for (const lens of lensSet) jobs.push(() => agent([SPEC, 'Adversarial finder on ' + FINAL_SCOPE, 'Your single lens: ' + lens, focus, 'Only real, actionable defects with concrete scenarios (schema). Read code deeply; no nits.'].join('\n'), { ...FABLE, effort: 'max', label: 'final:fable', phase: FTAG, schema: FINDINGS_SCHEMA }))
  if (round === 1) for (const lens of FINAL_OPUS_LENSES) jobs.push(() => agent([SPEC, 'Adversarial finder on ' + FINAL_SCOPE, 'Your single lens: ' + lens, 'Only actionable findings (schema).'].join('\n'), { ...MINOR, effort: 'max', label: 'final:opus', phase: FTAG, schema: FINDINGS_SCHEMA }))
  jobs.push(() => agent(codexRunnerPrompt(OUT + '/codex/final-r' + round, 'xhigh', codexReviewBrief('l\'INTERA feature #23: git diff issue23-baseline..HEAD piu i file nuovi; concentrati sulle interazioni tra fasi.', focus)), { ...RUNNER, label: 'final:codex', phase: FTAG, schema: CODEX_SCHEMA }))

  const res = (await parallel(jobs)).filter(Boolean)
  const codexRes = res.find(r => r && r.status)
  if (codexRes && codexRes.status !== 'done') log('Final review: codex ' + codexRes.status + ' - registrato')
  const fresh = res.flatMap(r => r.findings || []).filter(f => !seenF.has(fkey(f)))
  fresh.forEach(f => seenF.add(fkey(f)))
  const candidates = fresh.filter(f => f.severity !== 'low')
  const confirmed = []
  for (const f of candidates) {
    const v = await verifyFinding(f, FTAG, (f.severity === 'critical' || f.severity === 'high') ? 3 : 1)
    if (v.confirmed) confirmed.push({ ...f, verdict: v.reason })
  }
  log('Final review round ' + round + ': ' + confirmed.length + ' confermati')
  if (confirmed.length === 0) break
  if (round > MAX_FINAL_ROUNDS) {
    const critical = confirmed.filter(f => f.severity === 'critical' || f.severity === 'high')
    if (critical.length) return { aborted: 'final review: finding critical/high ancora confermati dopo ' + MAX_FINAL_ROUNDS + ' round di fix', open: critical, phases: phaseSummaries }
    finalOpenMediums.push(...confirmed)
    log('Final review: cap raggiunto, ' + confirmed.length + ' medium residui registrati nel report finale')
    break
  }
  await agent([SPEC, 'Fix these CONFIRMED findings from the final whole-feature review (root causes, real tests, no suppressions). Run build + full tests before finishing.', findingsList(confirmed), HOUSE_RULES].join('\n'), { ...DEVMAX, label: 'final:fix:r' + round, phase: FTAG, schema: DEV_SCHEMA })
  let g = await runGates(FTAG, 'final review fixes round ' + round)
  g = await devFixLoop(FTAG, { n: 'final', title: 'final review fixes', planSection: 'final', risk: 'high' }, g)
  if (!g || !g.ok) return { aborted: 'final review: gates rossi dopo i fix del round ' + round, phases: phaseSummaries }
  await commitFinalFixes(round, confirmed)
  finalLastFixed = confirmed
}

// completeness critic + synthesis
const critic = await agent([
  SPEC,
  'Completeness critic for the WHOLE #23 delivery (git diff issue23-baseline..HEAD). Answer: what is still missing versus (a) every decision in the decisions file, (b) every phase section and the test matrix (section 8) of the plan, (c) the original requirements in GitHub issue #23 (gh issue view 23), (d) the gates (docs/cheatsheet/reference/skills/UI types/CHANGELOG/version bump)? complete=true only if NOTHING is missing.',
].join('\n'), { ...JUDGE, label: 'final:critic', phase: FTAG, schema: CERT_SCHEMA })

if (critic && !critic.complete && critic.missing.length) {
  log('Final critic: ' + critic.missing.length + ' gap - ciclo di completamento')
  await agent([SPEC, 'Close every gap found by the final completeness critic, then run build + full tests:\n- ' + critic.missing.join('\n- '), HOUSE_RULES].join('\n'), { ...DEVMAX, label: 'final:complete', phase: FTAG, schema: DEV_SCHEMA })
  let g = await runGates(FTAG, 'final completeness'); g = await devFixLoop(FTAG, { n: 'final', title: 'final completeness', planSection: 'final', risk: 'high' }, g)
  if (!g || !g.ok) return { aborted: 'final completeness: gates rossi', phases: phaseSummaries }
  await agent(['Release clerk at ' + ROOT + ' (pre-authorized): stage all except review/orchestrator, commit via message file "chore(release): close final completeness gaps (#23)". No push.'].join('\n'), { model: 'opus', effort: 'low', label: 'commit:final:gaps', phase: FTAG, schema: DEV_SCHEMA })
}

const synthesis = await agent([
  SPEC,
  'Open medium findings accepted at the final cap (include them in the report): ' + JSON.stringify(finalOpenMediums.map(f => f.title)) + '. Synthesize the final report of the #23 delivery into "' + OUT + '/final-report.md" (write the file). Include: per-phase outcomes (read review/orchestrator/phase-*-report.md), final adversarial review outcome (rounds, confirmed/fixed findings, anything left open at low severity), perf baseline vs after (read the perf files), completeness critic verdict, the exact test totals from the last full run (run dotnet test if you need fresh numbers), commit list since the issue23-baseline tag (git log --oneline issue23-baseline..HEAD), and an honest "known limits" section (single-active-host contract, at-least-once, anything recorded as unresolved-low). End with a short GO/NO-GO for releasing 4.0.0. Return done=true and a 10-line executive summary in summary.',
].join('\n'), { ...JUDGE, label: 'final:synthesis', phase: FTAG, schema: DEV_SCHEMA })

return {
  phases: phaseSummaries,
  finalReport: OUT + '/final-report.md',
  executiveSummary: (synthesis && synthesis.summary) || 'synthesis missing',
}
