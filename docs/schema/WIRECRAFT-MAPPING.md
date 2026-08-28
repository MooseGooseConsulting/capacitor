# Wire-to-Schema Mapping Specification

This document defines the 1:1 mapping between client-side wire payloads (defined in `Capacitor.Cli.Core/Models.cs`) and the server's relational database schema.

---

## 1. Transcript Batch Mapping (`POST /hooks/transcript`)

### Wire Payload: `TranscriptBatch`
```json
{
  "session_id": "70dc37b2b3b14f139c153858abbe88a8",
  "agent_id": "subagent-1234",
  "lines": ["..."],
  "line_numbers": [1, 2, 3],
  "vendor": "claude",
  "strict": false,
  "repository": {
    "user_name": "developer",
    "user_email": "dev@example.com",
    "remote_url": "git@github.com:owner/repo.git",
    "host": "github.com",
    "owner": "owner",
    "repo_name": "repo",
    "branch": "main",
    "pr_number": 42,
    "pr_title": "feat: auth",
    "pr_url": "https://github.com/owner/repo/pull/42",
    "pr_head_ref": "feature/auth"
  }
}
```

### Destination Mapping:
1. **`session_events`:**
   * `session_id` $\leftarrow$ `batch.SessionId` (dashless)
   * `agent_id` $\leftarrow$ `batch.AgentId ?? ""`
   * `line_number` $\leftarrow$ `batch.LineNumbers[i]` (or position offset)
   * `vendor` $\leftarrow$ `batch.Vendor ?? "claude"`
   * `raw_payload` $\leftarrow$ `batch.Lines[i]` (parsed JSON)
   * `event_type`, `model`, `tokens`, `tools` $\leftarrow$ extracted by vendor normalizer
2. **`session_watermarks`:**
   * `(session_id, agent_id)` $\leftarrow$ updated to `max(line_number)`
3. **`sessions`:**
   * Updated with repo metadata if `batch.Repository` is present.

---

## 2. Session Title & Recap Mapping

### Wire Payload: `SessionTitlePayload` (`POST /api/sessions/title`)
* `session_id` $\to$ `sessions.session_id`
* `title` $\to$ `sessions.title`

### Wire Payload: `WhatsDonePayload` (`POST /api/sessions/whats-done`)
* `session_id` $\to$ `sessions.session_id`
* `content` $\to$ stored in session recap / events rollup.

---

## 3. Work Item Mapping (`POST /api/work-items/*`)

### Wire Payload: `declare_work_item`
* `session_id` $\to$ `work_item_sessions.session_id`
* `work_item_id` (or resolved from `issue_key` / `pr_number` / `new_title`) $\to$ `work_items.work_item_id`
* `correlation_source` $\to$ `'manual_mcp'`

### Wire Payload: `declare_work_breakdown`
* `parent_id` $\to$ `work_item_breakdowns.parent_id`
* `part_ids` $\to$ `work_item_breakdowns.part_id`

### Wire Payload: `declare_work_relation`
* `from_id` $\to$ `work_item_relations.from_id`
* `to_id` $\to$ `work_item_relations.to_id`
* `relation_kind` $\to$ `work_item_relations.relation_kind` (`'blocks'` | `'blocked_by'`)

---

## 4. Evaluation Run Mapping (`POST /api/sessions/{id}/evals/v3`)

### Wire Payload: `SessionEvalCompletedPayloadV3`
* `eval_run_id` $\to$ `eval_runs.eval_run_id`
* `judge_model` $\to$ `eval_runs.judge_model`
* `overall_score` $\to$ `eval_runs.overall_score`
* `summary` $\to$ `eval_runs.summary`
* `retrospective` $\to$ `eval_runs.retrospective_json` (JSONB)
* `retrospective_prompt_version` $\to$ `eval_runs.retrospective_prompt_version`
* `categories[].questions[]` $\to$ `eval_verdicts` rows:
  * `category` $\to$ `eval_verdicts.category`
  * `question_id` $\to$ `eval_verdicts.question_id`
  * `score` $\to$ `eval_verdicts.score`
  * `verdict` $\to$ `eval_verdicts.verdict`
  * `finding` $\to$ `eval_verdicts.finding`
  * `evidence` $\to$ `eval_verdicts.evidence`
  * `recommendation` $\to$ `eval_verdicts.recommendation`
  * `tools_used` $\to$ `eval_verdicts.tools_used`
  * `prompt_version` $\to$ `eval_verdicts.prompt_version`
* `facts_used[]` $\to$ logged with eval run context.
