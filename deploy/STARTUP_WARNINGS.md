# Deployment startup warnings

The API uses JWT bearer authentication with its configured `Jwt:Key`. It does not use authentication cookies, antiforgery payloads or persisted Data Protection links. Its Data Protection provider is explicitly process-local: no unused key files are written unencrypted to the container filesystem. SignalR connection tokens are process-local too; clients reconnect after a restart. Existing JWTs remain valid across restarts as long as their configured signing key and account state remain unchanged. The container test exercises login, an existing JWT and chat negotiation after a restart. The .NET 8 persistent-key-ring hosted preloader is removed specifically because it is unused by the ephemeral provider; application background workers remain registered. Runtime regression checks guard this framework-specific registration.

Before introducing cookie authentication, durable protected links or cross-instance SignalR connection-token handling, replace this provider with a shared persistent key ring encrypted at rest. Do not copy `Jwt:Key` into Data Protection or generate a new JWT signing key on every startup.

EF design-time startup registers only database services. It must not initialize Firebase, require a runtime JWT secret, or start background workers. Real server startup still requires its Firebase secret file and JWT configuration; missing credentials still fail startup. No Firebase private key belongs in the image or source repository. No migration is added in this change.

Dynamic JSON mapping is enabled per owned Npgsql data source in both the API and PhotoTools, instead of on the obsolete process-global mapper. PostgreSQL regression checks exercise the same configuration, including JSONB writes and migration rollback/reapply.

The container entrypoint validates `PORT` and binds `ASPNETCORE_URLS` to it. It clears the base image's conflicting HTTP/HTTPS port variables, rather than suppressing hosting log warnings. Render terminates public TLS as before.

CI checks the complete image build for the former EF-host startup error and obsolete mapper warning. Runtime smoke checks reject the former Data Protection/port warnings, check no key directory is written, and exercise the real auth endpoints, database readiness, migrations and restart.

After merging, deploy the latest backend commit in Render. No new Render environment variable, secret, database migration or frontend update is required for this change. Confirm that this deployment's build/startup logs no longer contain the four messages above; successful deployment alone does not certify live Cloudinary uploads or mobile flows.
