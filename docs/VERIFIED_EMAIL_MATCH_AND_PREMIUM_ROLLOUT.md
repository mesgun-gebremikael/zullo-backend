# Verified email, match notifications and Premium rollout

This release candidate changes registration and adds durable match notification work, multiple profile prompts and a verified Premium catalog. No production deployment, paid provider activation, merchant account setup or live customer charge is performed by this code change.

## Coordinated rollout and migration

The updated email client and API must be released together. Old clients expect registration `200` with a JWT; new registration is `202` with a pending email challenge. Existing account login remains compatible, but an old client cannot finish a new registration challenge. Do not add an unverified-account fallback to make that old client appear to work.

Apply the reviewed additive migration before starting workers/new API paths. It preserves existing User/Profile fields and introduces:

- Nullable `User.EmailVerifiedAtUtc`; legacy rows remain null rather than being falsely marked email-verified.
- `Profiles.ConversationPrompts`, JSONB with a valid empty-array default. Existing `PromptKey` and `PromptAnswer` remain intact and form the legacy read fallback.
- Pending email challenges with an email uniqueness constraint, challenge identity and expiry indexes.
- Match notification outbox rows unique on match/recipient, indexed for pending delivery, with a cascading foreign key to the match.
- `PushInstallations` with a device UUID, nullable account/token binding, monotonic version and unique non-null token hash; erasure retains an unbound version tombstone.
- Nullable `PurchaseGrants.ProviderTransactionId` and its lookup index for trusted RevenueCat aliases; the canonical store transaction key remains authoritative.

No existing accounts are deleted or silently linked to another identity. Reverting an applied migration can discard newly stored prompts/challenges/outbox/installation records; use forward fixes in production. Rollback/reapply checks run only in a generated disposable test database. The PostgreSQL migration verifier checks old field preservation, new defaults, Unicode prompt roundtrips/comparer changes, unique indexes and outbox cascade.

Before enabling public registration, configure and verify real SMTP delivery. Without a configured sender, new registration deliberately returns `503`, rather than issuing a JWT for a fake account or claiming that a code was sent. Keep all purchase gates disabled throughout this rollout.

## Email protocol and security

| Endpoint | Request | Result |
| --- | --- | --- |
| `POST /api/auth/register` | Email and password | `202` challenge with challenge ID, normalized email, expiry and next-send time; no User/JWT yet |
| `POST /api/auth/login` | Email and password | Existing account: normal authentication; correct pending-registration password: `202` resume challenge |
| `POST /api/auth/verify-email` | `challengeId`, six-digit `code` | Transactional code consumption and User creation, then normal auth response |
| `POST /api/auth/resend-verification` | `challengeId` | `202` replacement challenge when delivery/rate policy permits |

The server validates bounded email syntax and common provider-domain typos, such as `gmail.ocm`, without silently rewriting an address. Format validation or an MX record alone does not prove mailbox ownership. The emailed code is the ownership proof for new email accounts.

Codes are cryptographically generated, six ASCII digits, expire after 15 minutes and allow five verification attempts. Resending has a 60-second cooldown and a persisted five-sends-per-hour budget. Authentication endpoint/IP throttling remains in place. An email-keyed transaction lock serializes challenge changes across PostgreSQL replicas; the local semaphore is not the sole production protection.

Pending rows store a BCrypt password hash and a challenge-bound HMAC code digest, never a plaintext code. `Jwt:Key` is the server secret used for the code HMAC as well as JWT signing; protect it and keep it consistent across API replicas. Rotating it invalidates in-flight codes, which then require a new code. Constant-time digest comparison and transactional consumption prevent replay; successful consumption clears pending password/code hashes. Account erasure removes matching pending registration data before anonymizing the account email. The cleanup worker runs hourly and removes challenges whose expiry is more than 24 hours old.

The send budget and new challenge are committed before external SMTP I/O. Transport failure does not refund the budget. Only SMTP-accepted delivery is confirmed; a disconnect failure after acceptance does not invalidate a delivered code. A bounded independent confirmation write preserves accepted-delivery proof if the requesting browser closes. Connect, TLS, authentication and send failures are not hidden as delivery success.

Legacy `IsVerified` historically represented a login method, not this new ownership proof. Existing users are preserved rather than retroactively marked verified or unexpectedly locked out. Any later legacy-account email-verification policy needs its own explicit rollout decision. Matching email strings do not silently merge Google and password identities.

### SMTP configuration

Set these in the deployment secret/environment system. Never commit passwords, service keys or actual verification codes:

| Environment key | Required value/meaning |
| --- | --- |
| `EmailVerification__Smtp__Enabled` | `true` only when the sender is configured/tested |
| `EmailVerification__Smtp__Host` | Owner's actual SMTP host |
| `EmailVerification__Smtp__Port` | Provider's TLS port, valid integer 1–65535 |
| `EmailVerification__Smtp__FromAddress` | A provider-approved sender address |
| `EmailVerification__Smtp__Security` | Exactly `StartTls` or `SslOnConnect` |
| `EmailVerification__Smtp__Username` | Provider username when authentication is required |
| `EmailVerification__Smtp__Password` | Matching secret password when username is configured |

There is no plaintext `None`/opportunistic TLS mode or certificate-validation bypass. The MailKit transport has a 12-second overall delivery budget in addition to bounded I/O. Configure provider DNS/sender verification and check actual inbox/spam placement; local fake-sender tests do not establish deliverability. Security email instructions currently use English; the code and in-app verification UI remain usable with the app's localized instructions.

## Notification protocol and limits

A newly committed mutual match adds two IDs-only notification rows in the same pair-locked transaction: one per recipient. Retrying an already existing match does not enqueue a second pair. Dispatch rechecks the current match, both accounts and reverse blocks. Pausing discovery does not mute already existing chat or match notifications. Match removal cascades its outbox rows; closure/blocking cannot grant fresh access through notification delivery.

The worker polls every three seconds, processes bounded batches, applies a five-second send budget and bounded exponential backoff, and stops eligible delivery after 24 hours. Stable Android tags/collapse keys and APNs collapse IDs reduce duplicate presentation. FCM acceptance followed by an uncertain DB commit remains an at-least-once boundary, not a promise of mathematically exact-once OS delivery. Provider/OS focus settings, permission, offline state and already-accepted notifications remain outside the server's recall control.

Match/Superlike native strings come from the same translated source catalogs. Android resource localization and APNs localization use the OS language. Flutter's manual language setting does not itself override native OS localization. Foreground/cold handlers validate the current recipient/session and resolve trusted current profile/access before routing. They do not bypass login or profile onboarding.

Shared-device safety is implemented through versioned installation/token ownership. The client persists an encrypted UUID/version across logout; server installation/token locks reject stale claims and unlinks. Ambiguous legacy ownership is skipped. Message OS alerts contain generic localized copy without sender names, message text or avatars. Recipient/session checks still precede opening trusted current profiles. Already accepted OS alerts cannot be recalled reliably. Current delivery uses one mirrored `User.DeviceToken` destination per account, not multi-device fanout.

Message pushes are best effort after message persistence, unlike the durable match outbox. A provider failure does not undo the saved message; authenticated conversation history remains the source of message contents. Physical shared-device/account-switch checks remain required.

## Premium catalog and server authority

Recommend one ZULLO Premium tier, monthly and annual, with identical benefits. Do not introduce weekly billing until the stores, cancellation/upgrade UX and conversion data justify the extra complexity.

| Product | Type | Effect |
| --- | --- | --- |
| `zullo_premium_monthly` | Monthly subscription | RevenueCat entitlement `premium` |
| `zullo_premium_annual` | Annual subscription | Same entitlement `premium` |
| `zullo_premium` | Google subscription alias/base-plan family | Recognized monthly/annual native packages; verify actual base-plan metadata in sandbox |
| `zullo_hide_activity_monthly` | Legacy subscription only | Entitlement `activity_privacy`; preserve existing buyers, no new upsell |
| `zullo_superlikes_5` | Consumable | Five purchased Superlikes |
| `zullo_boost_1` | Consumable | One purchased Boost |
| `zullo_boosts_5` | Consumable | Five purchased Boosts |

Premium grants ordinary-like quota bypass, eligible rewind, ordinary incoming-like identities, incognito, activity hiding, three weekly included Superlikes and one included Boost per anchored month. Annual subscription billing must not make the Boost allowance annual. Included allowances do not roll over; purchased credits persist until used. Expired membership does not spend stale included balances. Refunds reverse their ledger grant once, including debt for already-spent credits; displayed purchased credit is clamped to nonnegative spendable amounts.

A Superlike is not a match. It deducts once, remains visibly distinct and available to its free recipient, and still requires mutual liking for chat. A 30-minute Boost changes ranking only among already eligible candidates. It does not expose a public paid marker, guarantee views/matches or bypass filters/privacy. Incognito and activity settings are server-enforced, not client-only presentation.

`GET /commerce/catalog` publishes known plans/benefits. State, sync, Boost and privacy operations are authenticated. `POST /commerce/purchase-status` checks the authenticated user's verified consumable grant for store, known product and an explicitly named identifier namespace (`store`, the compatibility default, or `revenuecat`). A provider alias is accepted only when authenticated provider evidence maps it to a canonical store grant owned by that user. Missing or ambiguous mappings fail closed. It does not accept client balance changes or SDK success as entitlement proof.

### Implemented receipt identity boundary and activation gates

The pinned `purchases_flutter 10.19.0` bridge uses `PurchasesHybridCommon 19.17.0`, Android Purchases `10.30.0` and iOS RevenueCat `5.99.0`.

- A successful `PurchaseResult.storeTransaction.transactionIdentifier` represents a native store transaction: the hybrid Android `StoreTransactionMapper` uses Google `orderId`; the iOS native store transaction has its StoreKit transaction ID.
- `CustomerInfo.nonSubscriptionTransactions.transactionIdentifier` represents the **RevenueCat-created** transaction ID. The pinned Android `Transaction` constructor reads JSON `id` separately from `store_transaction_id`; pinned iOS `NonSubscriptionTransaction` documents separate RevenueCat and store identifiers. The hybrid customer-info mapper does not expose the store field through this Dart property.
- The client preserves these namespaces, and the server records provider aliases only alongside a verified store transaction ID. Alias lookup remains account/product/store scoped and rejects ambiguity. A receiptless/restart recovery path cannot relabel a customer-info ID as a store ID. Never mint a balance from the client-supplied RevenueCat alias, a matching product/date alone or an observed balance increase.

Reviewable upstream evidence:

- `RevenueCat/purchases-hybrid-common`, tag `19.17.0`: `android/.../mappers/StoreTransactionMapper.kt`, `TransactionMapper.kt`, `ios/.../StoreTransaction+HybridAdditions.swift`, `NonSubscriptionTransaction+HybridAdditions.swift`.
- `RevenueCat/purchases-android`, tag `10.30.0`: `purchases/src/main/kotlin/com/revenuecat/purchases/models/Transaction.kt`.
- `RevenueCat/purchases-ios`, tag `5.99.0`: `Sources/Purchasing/NonSubscriptionTransaction.swift`.

Native checkout guards reserve before a charge, persist by original ZULLO account across restart/logout, and clear only on a definite cancellation or verified reconciliation/refund. An earlier receipt within the same product/date window must not confirm a later unknown checkout, and a concurrent other-device purchase must not clear the wrong attempt. Provider identity mapping proves a transaction, not automatically its association with a particular interrupted client attempt. Client-local receipt metadata is not an entitlement. **Billing activation gates remain:** authoritative correlation of receiptless interrupted attempts is not implemented, so those guards deliberately remain blocked; the updated app now performs a fresh provider-verified server sync before subscription checkout and blocks charging on an existing entitlement, disabled checkout or sync failure. This mitigates stale views but is not an atomic two-device purchase reservation. Test native store IDs, RevenueCat aliases, webhook IDs and refund IDs together in both stores before activation.

### Commerce configuration

Keep `Commerce__Enabled=false` until the owner explicitly approves activation after sandbox testing. Required server settings:

- `Commerce__RevenueCatSecretKey`: RevenueCat `sk_...` secret.
- `Commerce__WebhookAuthorization`: a long random exact Authorization-header value, at least 32 characters, also configured in RevenueCat.
- `Commerce__AllowSandbox=true` only for non-production testing. Production rejects sandbox records/events.

Configure webhook URL `https://<api-host>/commerce/webhook`. The endpoint verifies authorization, refetches provider state and accepts only known products/stores/environments. Credits use canonical store transaction keys; refunds leave idempotent tombstones. Legacy v1 purchase records lacking a store transaction ID wait for trusted provider evidence rather than being double-granted under a RevenueCat ID.

Flutter receives only public platform SDK keys and actual HTTPS legal URLs: `REVENUECAT_IOS_PUBLIC_KEY`, `REVENUECAT_ANDROID_PUBLIC_KEY`, `ZULLO_TERMS_URL`, `ZULLO_PRIVACY_URL`. Local store prices/currency and subscription terms come from the configured products. Never hardcode an invented charge amount or place server secrets in an app build.

## Discovery and profile compatibility

Foreground GPS sync is consented, rate-bounded, stopped in the background and refreshed on resume. Cumulative small movements refresh discovery; manual checking remains available for retry. Invalid/nonfinite/low-quality native fixes and stale-account updates are rejected. Turning tracking off wins an older in-flight request.

Strict discovery preferences are applied before any fallback. Relaxed distance/age searches require the saved relevant opt-in and an empty strict candidate pool. Expanded ages remain 18+ and at most five years beyond the requested interval. Gender, privacy, incognito, block and account-state gates remain intact. Unknown legacy `0,0` coordinates do not produce artificial nearby candidates.

Profiles support up to three distinct recognized prompt keys and 250-character answers. New APIs/JSON drafts preserve all answers; old first-prompt fields remain the projection/fallback. An old-client first-prompt edit must preserve the other two rather than erasing them. Existing profile data receives a valid empty-array default and remains readable without backfilling personal answers.

## Release evidence and remaining checks

Record final CI results against the final commit after all integration/security patches. The previous local pass counts are not proof of a later commit. Required automated coverage includes SMTP failure/acceptance boundaries, code expiry/replay/rate races, exact receipt/alias verification and refunds, annual monthly allowances, push ownership stale/concurrent claims, match outbox retries/revocation and real PostgreSQL migration preservation.

Before launch, complete native store sandbox purchase/restore/renew/refund/pending/interruption tests; actual SMTP delivery; signed Android/iPhone foreground/background/cold notifications and account switching; GPS permissions/travel; real photos; secure-storage v9→v10 upgrade; native-speaker/font/RTL review and store signing/provisioning. Debug/simulator builds alone do not prove production billing, OS delivery, payouts or release performance. These checks must remain explicit rather than described as “perfect.”

## Recommended mail provider

Resend is the recommended initial provider; see [RESEND_SETUP.md](RESEND_SETUP.md) for actual SMTP/TLS keys, verified-domain setup, quota considerations and owner-only secret configuration. No provider account, domain purchase or paid service has been activated.
