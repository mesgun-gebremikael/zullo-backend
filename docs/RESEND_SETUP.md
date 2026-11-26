# Verification email with Resend

Resend is the recommended initial transactional email provider for ZULLO. The application already uses provider-independent SMTP with required TLS; no additional runtime service or paid activation is needed in code.

Official references checked during this review:

- https://resend.com/docs/send-with-smtp
- https://resend.com/docs/dashboard/domains/introduction
- https://resend.com/pricing

The current Free plan lists a 100-email daily limit. Treat this as a small-pilot limit, not worldwide-launch capacity. Review the provider's current quotas before launch; no account or paid plan has been created or activated by this work.

## Owner setup

1. Register a domain you own, or use an existing one. Domain purchase is a separate owner decision; do not assume any ZULLO domain is available or owned.
2. Create your Resend account and add a dedicated sending subdomain, for example `auth.example.com` (replace the reserved example with your actual domain).
3. Add the exact DNS verification/sending records Resend supplies, wait for its verified status, and follow its domain guidance. Do not invent DKIM/SPF values.
4. Create a sending API key, restricted to the verified sender/domain where the provider permits it.
5. In the Render backend service's secret environment settings, configure the following. Never put the real key in Git, screenshots, logs or chat.

```text
EmailVerification__Smtp__Enabled=true
EmailVerification__Smtp__Host=smtp.resend.com
EmailVerification__Smtp__Port=465
EmailVerification__Smtp__Security=SslOnConnect
EmailVerification__Smtp__Username=resend
EmailVerification__Smtp__Password=<secret Resend sending API key>
EmailVerification__Smtp__FromAddress=noreply@auth.example.com
```

Replace `auth.example.com` with your verified sending domain. SMTP credentials here intentionally use the documented username `resend` and the API key as password. An alternative is port 587 with `StartTls`; use the paired explicit security mode, never opportunistic or unencrypted SMTP.

## Rollout and verification

The companion updated app understands `202` plus an email-code challenge. Older app versions that expect an immediate signup JWT cannot complete the new registration flow. Configure delivery and coordinate the backend/client rollout before inviting real signups.

Without valid delivery settings, new signup returns `503`; it does not create an unverified User or pretend to send an email. Existing legacy login is preserved. SMTP acceptance alone is not inbox delivery or email ownership: only the correct unexpired code completes signup.

After owner configuration, test using an inbox you own: registration, spam/inbox placement, correct code, wrong/expired code, resend cooldown, quota/delivery failure and retry. Check the provider dashboard without exposing API keys or verification codes. Do not use a real stranger's address for testing. These real-provider/inbox checks are not claimed by local fake-transport tests or by successful app compilation.

If quota, authentication, DNS or TLS fails, fix the configuration/provider state; never enable a bypass that issues a User/JWT without proving the email address. SMTP verification is separate from store billing; this setup does not activate Premium purchases.
