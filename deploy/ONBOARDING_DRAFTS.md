# Private onboarding drafts

Apply `PrivateOnboardingDrafts` before the matching frontend. GET/PUT `/me/onboarding` are authenticated and owner-scoped; no user ID is accepted from the payload. The JSON schema is bounded, validates types/field lengths against profile constraints, and does not accept verification, visibility, payment or account-state fields.

PUT uses a revision plus a whole snapshot. The owner's account lifecycle/row locks serialize writes with completion and closure. Identical replay is idempotent even after PostgreSQL JSONB normalization; stale changed snapshots return 409. An empty draft is not a public Profile and does not appear in discovery. New public profile creation requires two owned images; existing profile editing remains compatible.

Saving selected draft photos validates owner/state/expiry and extends ready assets to seven days. Unselecting a previously registered draft image queues normal cleanup. GET filters expired/deleted photos and reports them. A rejected expired-photo write supplies only still-valid URLs owned by the caller, allowing the client to retain other answers and re-upload the missing image.

Profile save removes the private draft in the same transaction. Account closure removes it under the exclusive lifecycle lock and queues all owned photos, including draft assets. Establish a retention policy for inactive draft text before public launch; it is currently retained until completion/closure, independently of the shorter image expiry.

Checks include ownership isolation, no publication, schema/type errors, revision replay/conflict, image ownership/retention, removal on completion/closure, and PostgreSQL concurrent writes plus migration replay. No live customer database was migrated or erased during development.
