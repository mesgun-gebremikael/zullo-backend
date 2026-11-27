# Signing-key deployment guard

A serving API must use a configured `Jwt:Key` of at least 32 UTF-8 bytes. Outside the `Development` environment the public development example from appsettings.json is rejected, including whitespace/case variants. This prevents an accidental deployment with the repository's known example key. Key length alone is not proof of entropy: generate deployment credentials from a cryptographically random source and keep them in the owner's secret system, never Git/chat/screenshots/logs.

Set `Jwt__Key` in the deployment environment. Use distinct private keys for separate environments. No live key is generated, replaced or rotated by this change. Check the existing deployment configuration before rolling out the updated API. If a real deployment currently uses the example/short key, schedule replacement deliberately: replacing a signing key invalidates existing signed login tokens and outstanding email-code hashes, so do not silently rotate a live key as part of code review.

The check runs before Firebase/other runtime services initialize. EF design-time model construction remains exempt because migration generation does not serve requests or issue tokens. The published container still contains no development connection/JWT defaults and receives its real configuration externally.

Unit tests cover missing/short keys, UTF-8 length boundaries, preserving the supplied value, and development-only acceptance of the public example. Container tests boot the real image with synthetic Firebase credentials and reject both a short key and the known public example in Staging. Existing good/random-key startup, authentication and restart checks remain in place.

This guard does not configure issuer/audience policies, rotate the owner's existing secrets, enable email delivery, publish production or activate billing.
