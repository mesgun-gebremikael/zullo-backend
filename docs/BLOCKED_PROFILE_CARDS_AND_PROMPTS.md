# Blocked profile cards and conversation prompts

## Behavior and privacy

`GET /blocks` remains authenticated and returns outgoing blocks only. Each item adds optional `age` and `photoUrl` fields to the existing `userId` and `displayName` contract. Old clients can ignore these additive fields. It does not grant access to `GET /profiles/{id}` or reveal a full private profile, biography, activity or coordinates.

A newly created block records the name, age and first photo already accessible to the blocker at that moment. This identification snapshot is not refreshed by repeated block requests or private profile edits. The API only returns the recorded image while it remains in the person's current photo list. A reverse block, missing profile or closed account redacts the card. Hidden, paused or active incognito profiles cannot be captured through an arbitrary block request unless an existing match permits access (or the incognito person already liked the blocker). Legacy blocks have no historical snapshot: current public profile identification is a conservative fallback; inaccessible profiles get a neutral card.

Account removal removes both incoming and outgoing blocks, including snapshots. Unblocking still deletes the old conversation and requires new reciprocal likes; this change does not resurrect chats. Snapshot lookup uses dictionaries and sets, avoiding repeated linear scans per blocked card.

## Deployment

Apply additive migration `20261103035014_BlockedProfileReferences` with the backend update before deploying the new frontend. It adds nullable age and empty-default name/photo columns; existing profiles, block relationships and data are preserved. No backfill of private profile data is performed. Do not roll back the migration on production after storing snapshots: its generated `Down` removes only these new columns and therefore loses snapshot identification. Forward updates are preferred.

## Profile choices

Seven more stable prompt identifiers are accepted: `perfect_evening`, `travel_story`, `food_opinion`, `teach_me`, `simple_plan`, `surprise`, `talk_all_night`. Existing five identifiers remain valid. The client localizes questions, while user answers are stored unchanged. `Friendship` uses the existing intention string field; skipping religion stores an empty string. Neither requires a data migration.

## Validation

Integration coverage exercises accessible snapshots; repeated block requests and private edits; deleted images; hidden, paused, incognito and reverse-blocked targets; redaction after a reverse block or account closure; account removal erasing snapshots; and persistence of all new prompt identifiers with multilingual user text. PostgreSQL migration verification seeds a legacy block and checks defaults, relationship preservation, repeat migration, rollback and reapply in a disposable test database only.
