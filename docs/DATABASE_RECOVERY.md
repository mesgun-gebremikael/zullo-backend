# Database recovery and restore verification

A reachable database and a successful `pg_dump` do not establish that a usable backup exists. ZULLO needs an owner-configured backup/retention policy and a proved restore procedure before production launch. This code change does not enable a hosted paid backup service, connect to Render, copy real customer records or alter a production database.

## Code-only restore drill

Run from the repository root on an isolated machine with Docker and the pinned .NET8 SDK:

```bash
python3 Zullo.Api.Tests/verify-backup-restore.py
```

This verifier accepts **no production connection, existing dump or target database input**. It creates a uniquely named PostgreSQL16 Docker container bound only to a random localhost port, with a generated disposable password and uniquely named test databases. The container and temporary local directory are owned by this invocation and removed afterward. No existing database/container is erased.

The source is initialized using the application's current EF migrations and explicitly synthetic `example.test` accounts. Fixtures include Unicode profile prompts, chat and message sequence state, private onboarding data, verified-email state, pending email challenge state, purchased credits and a provider receipt alias, match outbox rows and an unbound versioned push-installation tombstone. No API, Firebase, SMTP, Cloudinary or payment worker is started.

The check performs actual PostgreSQL16 custom-format `pg_dump` and `pg_restore` into a fresh target. It compares every public table's complete row JSON against the pre-dump synthetic snapshot, including migration history. It checks no migrations are pending, saves a new chat message to prove the restored sequence progresses, and exercises the purchase ledger's restored unique constraint. A deliberately truncated archive must be rejected by real `pg_restore`.

A SHA256 and elapsed test duration are printed only for the synthetic archive/drill. The checksum is an integrity identifier, **not** proof that a real remote backup is protected, recent, complete or restorable. The drill is deliberately separate from a real production recovery operation.

## Production prerequisites for the owner

- Choose the production database's backup/PITR capability, retention and recovery-point/recovery-time targets according to the actual service plan and business needs. Verify the provider's current guarantees; free staging storage is not a production backup policy.
- Keep backups encrypted in access-controlled storage with recovery access independent of the failed deployment. Track age/failure alerts and access logs. Credentials and export files must not enter Git, chat, CI artifacts or public logs.
- Back up required external configuration through an approved secret system. The database does not include Apple/Google credentials, signing/upload keys, provider settings or Cloudinary image bytes. Configure provider retention/recovery separately.
- Periodically restore an owner-approved real backup to a new, isolated recovery database. Check recorded version/size/integrity, expected schema/migration history and application-level invariants with least-privilege access. Do not overwrite the existing production database to perform a drill.
- Keep the recovery application isolated with SMTP, push, payment webhooks/workers and photo-erasure workers disabled. Restored pending outbox/erasure rows must not send old alerts or delete live provider assets during verification. Review replay/deduplication and provider state before cutover.
- Reconcile current store transactions/refunds after restoring an older point in time. Restored wallet balances alone cannot prove recent provider state, and backups must not be used to regrant spent/ refunded purchases. Retain account-erasure obligations and deletion logs across disaster recovery so previously erased users are not silently republished.
- During an actual incident, document the selected backup, recovered data cutoff, validation, owner-approved cutover and rollback plan. Production mutation/publication remains a separate owner decision. Never perform destructive schema rollback as a substitute for a recovery plan.

## Existing health checks

`/health/live` checks that the process is alive; `/health/ready` checks database connectivity and applied EF migrations with a five-second budget. They return generic health responses rather than credentials or internal SQL details. Existing container integration tests already cover pre/post migration readiness, database outage/recovery and process restart. Readiness intentionally does not claim that optional SMTP/billing providers are configured or that a backup can be restored.
