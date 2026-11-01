# Owned photo storage and erasure

New uploads are authenticated backend operations. The app never receives the Cloudinary API secret, uses an unsigned upload preset, or supplies a public ID to delete. Profile changes accept newly added photos only from the caller's ready/attached records. Existing profile URLs remain readable during migration.

## Activation order

This change has not been deployed or exercised against a real Cloudinary account yet. All automated provider tests use a controlled HTTP transport. Do not announce production erasure as complete before the live checks and legacy review below.

1. Back up the target database and deploy the backend with `Photos__Enabled=false`. Apply the additive `OwnedPhotoStorage` migration using the existing controlled migration command. Do not roll it back after accepting photo jobs: that would discard the durable cleanup queue. The new app requires this backend.
2. In the server's secret environment, configure `Cloudinary__CloudName` (current app used `dzdugvbbr`), `Cloudinary__ApiKey`, and `Cloudinary__ApiSecret`. Obtain these in the Cloudinary console; never put the secret in Flutter, source control, PRs, logs or chat. The credentials must support image upload, Admin deletion by asset ID and Admin resource inspection for that cloud. Use a separate test cloud for staging when possible.
3. Disable the old unsigned `zullo_upload` preset and audit other unsigned presets so they cannot write/overwrite the `zullo_owned/` namespace. Existing image delivery continues without the preset. Old app builds must be replaced; their direct uploads cannot be attached by the new API. Coordinate this initial rollout as an upload maintenance window instead of allowing unsigned uploads to continue.
4. Set `Photos__Enabled=true`, release the matching frontend and perform the live checks below. Missing/disabled configuration returns a readable unavailable response, preserving saved images. Configuration affects both uploads and the deletion worker; disabling it pauses cleanup, so monitor outstanding jobs.
5. Review historical ownership with the operator tool. New managed images need no manual approval; old images require a one-time reviewed mapping.

The migration contains no provider credentials and makes no external deletions. No hosted worker is started by unit tests or the operator tool.

## Lifecycle and failure handling

- Maximum upload: 10 MiB, JPEG/PNG/WebP headers only. Cloudinary performs actual decoding, limits stored dimensions to 1920×1920 and strips image metadata. Malformed/unsupported images never become published URLs. HEIC selection relies on the mobile picker converting it; confirm real iOS selection in the live checks.
- Each upload has a UUID and a durable owner-bound reservation before network I/O. Repeating that UUID with the same bytes reuses the operation; different bytes or a different owner cannot replace it. A retry reconciles an earlier provider result before uploading again.
- At most 12 unfinished/live managed images per owner and 50 upload records per day; request rate limiting also applies to upload attempts. The profile still allows six images. Attached images remain safe when provider credentials are unavailable.
- A successful draft expires after 24 hours unless a profile save attaches it. Removing an image in the editor alone does not delete the saved image. Saving the removal queues deletion atomically with the profile change. Cancelling the editor preserves the old saved photo.
- Account closure queues every managed image, including drafts and uploads in progress. A late provider response cannot reopen the account or reattach an image. Pausing an account never queues its pictures for deletion.
- The worker runs every minute and claims at most five jobs per pass using database leases. It shares owner locks with profile saves/closure, rechecks live references and performs network I/O outside transactions. Multiple workers can cooperate safely. An unresolved upload waits at least two hours, beyond the provider signature lifetime, before orphan cleanup.
- Deletion verifies the resource's owner context and stable asset ID, then deletes by the immutable asset ID with `invalidate=true` and confirms its absence. Deletion cannot accidentally target a replacement that reused the public ID. A missing provider resource is an idempotent success. Failures remain in PostgreSQL, retry with backoff (up to 24 hours) and produce sanitized error codes. Ownership inconsistencies go to review instead of destroying a doubtful resource.
- Cloudinary CDN invalidation is asynchronous. Previously downloaded copies and external backups cannot be recalled by the app. Document provider backup/retention settings in the privacy policy and configure them before launch.
- A completed record retains a minimal technical tombstone (IDs, status, timestamps), clears its URL/aliases/file hash and prevents stale operations from reviving it. Retention of these technical identifiers and account-removal records still needs an explicit business/privacy retention policy.

## Legacy review (operator only)

Historical unsigned uploads cannot be proven to belong to an account merely because its profile lists a URL. The tool proposes a mapping for a human to verify; the dry run is **not proof of ownership**. It refuses foreign hosts/clouds/namespaces and shared assets, verifies Cloudinary's current asset ID, and checks that references did not change between planning and approval. Unknown/shared/missing resources stay in `legacy_review`; investigate them individually, including Cloudinary records/backups. Do not clear the queue merely to mark a request complete.

The runtime image includes `/app/photo-tools/PhotoTools.dll`. Run in a protected operator shell with the intended server environment (never copy secrets into the command line). For local development, `dotnet run --project PhotoTools -- ...` is equivalent. The tool accepts the same Npgsql or PostgreSQL URL `ConnectionStrings__Default` setting as the web server and checks its deployment database guard as well.

```sh
# Set PhotosAdmin__ExpectedDatabase in the protected environment to the exact
# database name being reviewed, in addition to the server/provider settings.
dotnet /app/photo-tools/PhotoTools.dll --plan /tmp/photos-review.json
# Inspect the local file securely: account identity, source URLs and provider
# asset ownership. Do not upload this personal-data file to a PR or chat.
# Only after review, use the exact SHA256 printed by the dry run:
dotnet /app/photo-tools/PhotoTools.dll --apply /tmp/photos-review.json --approve-sha256 REVIEWED_SHA256
```

Planning writes only a new local file (0600 on Unix); it changes no database/provider resources. Apply records the approved mapping and queues unlinked/closed assets for the normal worker. The digest is an explicit operator approval, not cryptographic evidence of the historical user's ownership. The worker rechecks the approved stable asset identity before deletion. Run further plans in batches of at most 50. Remove local review files after handling them securely.

## Required live checks before launch

Use synthetic test accounts and real valid test images, never customer photos for destructive testing.

- Upload JPEG/PNG/WebP on Android and iOS, including gallery HEIC conversion and a camera-originated photo. Verify orientation, visible quality, dimensions and metadata removal in Cloudinary.
- Save two images, restart/relogin and verify profile order, previews and discovery delivery. Cancel a later edit: the saved images must remain.
- Remove and save a managed image; observe `delete_pending` become `deleted` and confirm the resource is gone in Cloudinary. Allow time for CDN invalidation.
- Simulate provider outage in an isolated test environment. Confirm the job survives an API restart and completes after connectivity/configuration returns, without deleting a currently published image.
- Close a synthetic account during an upload, confirm JWT rejection/public-profile removal and eventual deletion of all managed images. Confirm a paused account retains its images.
- Dry-run/review/apply a synthetic legacy image and verify the same cleanup. Verify a deliberately shared legacy resource stays in review.
- Mobile network interruption, repeated taps, picker cancellation, expired login and account switching must show readable errors without losing saved images. User's emulator tests remain pending until the emulator problem is resolved.

## Monitoring

Alert on growing or old `delete_pending`/`legacy_review` queues, recurring `LastErrorCode`, and unreviewed removal requests. Do not log private image URLs, API responses, multipart bodies or provider credentials. A low-volume single worker processes at most five assets per minute; review queue age and tune capacity against the Cloudinary Admin API rate limit before increasing traffic. A free/sleeping Render instance cannot provide a guaranteed background-erasure deadline: use an always-on worker before public launch and test restart recovery.

```sql
SELECT "State", count(*), min("CreatedAtUtc") FROM "OwnedPhotos" GROUP BY "State";
SELECT "LastErrorCode", count(*) FROM "OwnedPhotos"
 WHERE "State" IN ('delete_pending','legacy_review') GROUP BY "LastErrorCode";
SELECT count(*), min("CreatedAtUtc") FROM "AccountRemovalRequests"
 WHERE NOT "ExternalAssetsReviewed";
```

Automated coverage includes authenticated ownership, idempotency, configuration failure, malformed/provider responses, signed uploads, immutable-ID deletion, confirmed absence, stable-ID checks, draft expiry, commit-only removal, retry/backoff, closure during upload, legacy approvals, changed/shared references and real PostgreSQL concurrency/migration replay. These checks reduce risk; real provider and mobile checks above remain necessary.
