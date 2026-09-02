# Remote validation

## Rule

Run broad Capacitor validation remotely. Local end-to-end runs can trigger
interactive `allow` prompts and produce failures unrelated to the actual
system; they are not the acceptance path for this recovery.

Unit and contract tests run on the existing self-hosted runner. PostgreSQL
integration and end-to-end import tests run remotely against Blood Arrow's
isolated `capacitor_test` database. A test that needs PostgreSQL must not fall
back to SQLite, a local temporary database, or a mock database.

## Connection and secret handling

The test driver reaches Blood Arrow through the host and a short-lived remote
port-forward to `pg18-core-recovery-rw`; build/test execution occurs in the
remote environment. Its password is injected from the remote Kubernetes
basic-auth Secret `data-platform/capacitor-test-db-credentials`. Doppler
`homelab/dev` secret `CAPACITOR_TEST_DB_PASSWORD` is the source of truth used
to provision or rotate that Secret, not a value printed or stored by the test
driver. Commands, logs, artifacts, and Git contain only secret names, never
secret values or full connection strings.

Provisioning gives Capacitor its own role, database, and Kubernetes basic-auth
Secret. Tests never use the shared `app` role. No test deletes, resets, or
changes another application's database; test rows are run-scoped and cleanup,
when needed, is limited to that run's rows.

## Required evidence from each adapter

For every vendor fixture, run two imports of the same source records and verify:

- the expected canonical event ordering, including multiple events from one
  source line;
- raw payload retention and typed content, thinking, tool, error, and usage
  fields;
- correct token/cache/cost totals and session/trace rollups;
- no duplicate events after replay; and
- atomic contiguous watermark advancement.

Representative Claude, Codex, and subagent fixtures must then drive the web
Sessions list, Transcript, Events, and Trace views. HTTP success alone is not
acceptance. A result is reported as remote PostgreSQL-tested only when the test
actually reached Blood Arrow's `capacitor_test` database.
