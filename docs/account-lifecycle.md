# Account controls and delivery limits

## Implemented behavior

- Four navigation destinations: Swipa, Likes, Chattar, Profil. Named tab indices keep push/chat routing correct after removing Explore. Remove nonfunctional Duos/Astro modes and the dead lower rewind button. The remaining rewind is the existing server-backed operation. The edit action has a pencil, not a send icon; swipe actions have labels and 48 px header touch targets.
- `GET /me/account/status`, `PATCH /me/account/pause`: a paused account is excluded from discovery and incoming-like previews, and cannot swipe or spend Boosts. Existing matches/chat remain. The flag is independent of profile completion; editing photos does not reactivate an account. Swipa displays paused status with a route back to account controls.
- `POST /me/account/removal`: explicit confirmation and current password are required. Wrong passwords do not alter data; attempts are limited per account and IP. A successful request closes access immediately, anonymizes login/contact fields, disconnects push delivery, removes the public profile, chats, matches, likes/skips and blocks, and retains pseudonymous payment replay records and safety reports. JWT validation rejects a closed account on subsequent API requests and new hub connections. Existing devices receive AccountClosed; peers invalidate the removed relationship. Backend transaction commits before notifications.
- This is an **asynchronous erasure request**, returning 202 and a reference. It does not claim that every external copy is already erased. Subscriptions must still be cancelled in the native store; neither pause nor closing an account automatically stops billing.

## External asset follow-up is required

New owner-bound Cloudinary uploads have a durable deletion worker: saved photo removal and account closure queue assets, verify their immutable provider identity and retry until erasure is confirmed. Uploading/draft assets are covered, including responses that arrive after account closure. Pausing retains all photos. See [photo storage rollout](../deploy/PHOTO_STORAGE.md) for activation and monitoring.

Historical unsigned uploads still require a one-time human ownership review. The operator dry run proposes mappings, checks stable provider asset IDs and refuses ambiguous/shared references. Approval is required before the normal worker can erase these assets. `legacy_review` means unresolved follow-up, never completed erasure. Configure provider credentials, disable the old unsigned preset, complete real Cloudinary/mobile checks and review old records before claiming production readiness.

Establish retention/purging rules for closed-account keys, technical photo tombstones, purchase records, safety reports and provider backups. This change does not establish a legal retention policy or certify store/legal readiness.

## Deployment and validation

Apply `AccountLifecycle` before running the new backend JWT account-state check. Deploy backend first, then the app. Existing accounts default to active and open; no account or data is modified by applying the migration alone. No real account deletion or deployment is performed by development/testing.

Automated tests cover explicit confirmation, wrong-password handling, account attempt limits, pause/reactivate, preserved matches, blocked discovery/likes, old JWT rejection, pseudonymous ledger retention and pending photo follow-up. Existing registration/email, profile, chat/block, purchase and notification regression tests still run. Store-connected billing and all outstanding owner device tests remain pending because the owner's emulators are currently unavailable. Verification still requires a real provider integration and is not marked implemented.
