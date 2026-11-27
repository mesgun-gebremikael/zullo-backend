# Mobile hot-path review — 26 November 2026

## Discovery feed

Previously every feed refresh downloaded the viewer's entire outgoing like/skip/block history into the API process and separately queried every active Boost in the database. This made request memory and transferred database rows grow with account age and unrelated users worldwide.

The feed now keeps exclusions in indexed SQL `EXISTS` predicates and projects incoming Superlike/active Boost flags only for the bounded candidate query. The endpoint executes three database reads instead of eight, excluding authentication. The existing candidate cap, exact geographic filter, strict-before-fallback behavior, priority ordering and public DTO remain intact. Query cancellation follows a disconnected mobile request.

Existing unique indexes on like/skip/block user pairs and the commerce account primary key support these lookups; no schema migration is required. Boost activity remains private rather than a public profile flag.

## Purchase reconciliation

Provider synchronization previously fetched each historical receipt separately while holding the account transaction lock. It now resolves existing receipt ledger entries in one query, preserving globally claimed transaction ownership, product checks, spent credits, refunds and authenticated provider aliases. Duplicate provider entries still cannot grant a second credit.

Payments remain disabled until the separately documented store/provider setup, interrupted-purchase recovery and real sandbox checks are complete. This review does not activate billing or email delivery.

## Verification and limits

- Full server suite: 313 tests pass, including mixed spent/foreign/refunded/duplicate receipt reconciliation.
- Discovery regression checks forward/reverse blocks, outgoing likes/skips, incognito, Superlike/Boost ordering, distance eligibility and absence of public Boost fields.
- Real PostgreSQL chat/load harness checks provider SQL, privacy and concurrent wallet behavior using a disposable local database.

Synthetic API load is not a phone frame-time benchmark or a promise of production latency. Before launch, profile release/profile builds on representative iOS/Android devices, real image sizes, slow mobile networking and realistic geographic profile density. The legacy full-history message endpoint remains for compatibility; the mobile paginated endpoint is the supported bounded history path.
