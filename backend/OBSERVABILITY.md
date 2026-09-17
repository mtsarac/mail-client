# Observability & Operations

The API emits OpenTelemetry **traces** and **metrics**, writes structured JSON **log files**, and exposes ASP.NET Core **health checks**. None of this requires an external collector: with export disabled the application runs normally, and telemetry export failures never affect mail operations.

```text
MailClient API
    │  System.Diagnostics Meter "MailClient" + ActivitySource "MailClient"
    │  + built-in ASP.NET Core / Kestrel / runtime meters
    ▼
OpenTelemetry SDK
    ├── OTLP exporter (optional) ──► OpenTelemetry Collector ──► Prometheus / Grafana / Tempo / any OTLP backend
    └── /metrics (optional, Prometheus scrape)
```

## Configuration: static vs runtime

Everything on this page is **static deployment configuration** (`appsettings.json` / environment variables) under the `Observability` section. It is never stored in the database-backed runtime settings used by the Management API.

| Key | Default | Purpose |
|---|---|---|
| `Observability__ServiceName` | `mail-client` | `service.name` resource attribute |
| `Observability__Tracing__Enabled` | `true` | Collect traces |
| `Observability__Metrics__Enabled` | `true` | Collect metrics |
| `Observability__Otlp__Enabled` | `false` | Export traces and metrics over OTLP |
| `Observability__Otlp__Endpoint` | empty | Collector base URI; empty uses `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `Observability__Otlp__Protocol` | empty | `Grpc` or `HttpProtobuf`; empty uses `OTEL_EXPORTER_OTLP_PROTOCOL` |
| `Observability__Prometheus__Enabled` | `false` | Expose `GET /metrics` (requires metrics enabled) |
| `Observability__Logs__App__RetainedFileCountLimit` | `14` | Retained `logs/app-*.json` files |
| `Observability__Logs__Http__RetainedFileCountLimit` | `7` | Retained `logs/http-*.json` files |
| `Observability__Logs__{App,Http}__FileSizeLimitBytes` | `104857600` | Roll to a new file after this size |

Resource attributes: `service.name`, `service.version` (assembly informational version), `service.instance.id` (generated per process), `deployment.environment` / `deployment.environment.name` (ASP.NET Core environment). No account, mailbox or credential data is ever placed in resource attributes.

Standard `OTEL_*` variables (for example `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_RESOURCE_ATTRIBUTES`) are honoured. Collector credentials in `OTEL_EXPORTER_OTLP_HEADERS` are secrets: supply them through the deployment secret store, never commit them, and never log them.

## Health endpoints

| Endpoint | Question answered | Checks |
|---|---|---|
| `GET /health/live` | Is the process alive? Restart only if this fails. | None. Never depends on PostgreSQL, mail providers, OAuth providers, Firebase or internet access. |
| `GET /health/ready` | Can this instance serve core API traffic now? | `postgres` (lightweight `CanConnect`), `storage` (the configured attachment provider — local filesystem or S3-compatible bucket — is reachable and writable) |
| `GET /health` | Compatibility alias of `/health/live`. | None |

Readiness never contacts customer IMAP/SMTP servers, OAuth providers or Firebase. Each check has a 5 second timeout. Health endpoints bypass the user/API traffic rate limiter, so probes never receive `429`.

Status codes: `Healthy` and `Degraded` → `200`, `Unhealthy` → `503`. Response body:

```json
{ "status": "Healthy", "checks": { "postgres": "Healthy", "storage": "Healthy" } }
```

Responses never include connection strings, file paths, exception messages or account data.

Example Kubernetes probes:

```yaml
livenessProbe:
  httpGet: { path: /health/live, port: 8080 }
readinessProbe:
  httpGet: { path: /health/ready, port: 8080 }
```

## Prometheus endpoint

`GET /metrics` exists **only** when `Observability__Prometheus__Enabled=true`; otherwise it returns `404`.

- It uses `OpenTelemetry.Exporter.Prometheus.AspNetCore`, which is a **prerelease** package. Its use is isolated to `src/MailClient.Api/PrometheusMetrics.cs` and runs only when enabled. OTLP is the stable, production-neutral export path; prefer scraping an OpenTelemetry Collector if prerelease dependencies are not acceptable.
- Prometheus metrics come from the same `Meter` instrumentation as OTLP; there is no separate metrics model.
- The endpoint is **unauthenticated**, does not use mailbox JWTs or the Management API key, and bypasses the user/API traffic rate limiter so scrapes never receive `429`. Restrict it at the reverse proxy or network layer to trusted monitoring infrastructure (for example, allow `/metrics` only from the Prometheus network and deny it on the public ingress).

Example scrape configuration:

```yaml
scrape_configs:
  - job_name: mail-client
    metrics_path: /metrics
    scrape_interval: 30s
    static_configs:
      - targets: ["mail-client-internal:8080"]
```

## OTLP example architecture

```yaml
# otel-collector.yaml (sketch)
receivers:
  otlp:
    protocols:
      grpc: { endpoint: 0.0.0.0:4317 }
exporters:
  prometheusremotewrite: { endpoint: http://prometheus:9090/api/v1/write }
  otlp/tempo: { endpoint: tempo:4317, tls: { insecure: true } }
service:
  pipelines:
    metrics: { receivers: [otlp], exporters: [prometheusremotewrite] }
    traces: { receivers: [otlp], exporters: [otlp/tempo] }
```

```bash
Observability__Otlp__Enabled=true
Observability__Otlp__Endpoint=http://otel-collector:4317
```

Grafana then reads metrics from Prometheus and traces from Tempo. Startup does not wait for the collector; unreachable collectors only drop telemetry.

## Metric naming

One application meter, `MailClient`. Instruments use dot-separated OpenTelemetry names; the Prometheus exporter converts them (for example `mailclient.sync.completed` → `mailclient_sync_completed_total`). Durations are histograms in seconds.

| Area | Instruments | Dimensions |
|---|---|---|
| Sync scheduling | `mailclient.sync.scheduled`, `mailclient.sync.queue.rejected`, `mailclient.sync.queue.pending` (gauge) | `origin`, `reason` (`queue_full`, `shed`) |
| Sync execution | `mailclient.sync.completed`, `mailclient.sync.failures`, `mailclient.sync.duration`, `mailclient.sync.active.accounts`, `mailclient.sync.active.folders`, `mailclient.sync.connection_budget.wait` | `origin`, `folder_type`, `result`, `failure_category` |
| Sync recovery | `mailclient.sync.retries`, `mailclient.sync.retries.exhausted`, `mailclient.sync.recoveries` | `origin`, `folder_type`, `failure_category` |
| Distributed locks | `mailclient.lock.acquisitions` | `purpose` (`account_sync`, `oauth_refresh`), `result` (`acquired`, `contended`, `failed`) |
| OAuth | `mailclient.oauth.authorization.started`, `mailclient.oauth.authorization.completed`, `mailclient.oauth.token.refresh`, `mailclient.oauth.token.refresh.duration`, `mailclient.oauth.reauthentication.required` | `provider`, `result` |
| Send | `mailclient.mail.send`, `mailclient.mail.send.duration`, `mailclient.mail.sent_copy.failures` | `provider`, `result` (`success`, `failure`, `delivery_unknown`, `cancelled`) |
| Search | `mailclient.mail.search`, `mailclient.mail.search.duration`, `mailclient.mail.search.result_count` | `has_text_query`, `result` |
| Push | `mailclient.push.notifications`, `mailclient.push.failures`, `mailclient.push.invalid_tokens`, `mailclient.push.duration` | `event_type`, `result` |
| Discovery / connections | `mailclient.discovery`, `mailclient.discovery.duration`, `mailclient.mail.connection.failures` | `result`, `discovery_source`, `provider`, `protocol` (`imap`, `smtp`), `failure_category` |
| Runtime settings | `mailclient.runtime_settings.load_failures` | none |

Send failures are `mailclient.mail.send{result="failure"}`; sync failure categories come from the existing sync failure classifier, never from exception text.

Built-in meters collected instead of custom HTTP metrics: `Microsoft.AspNetCore.Hosting` (`http.server.request.duration`), `Microsoft.AspNetCore.Routing`, `Microsoft.AspNetCore.RateLimiting`, `Microsoft.AspNetCore.Server.Kestrel`, `Microsoft.AspNetCore.Diagnostics`, `System.Runtime`. The `System.Net.Http` client meter is intentionally not collected because its `server.address` label would include customer-controlled discovery hosts.

## Cardinality and privacy rules

**Metrics and span attributes must use bounded-cardinality, non-identifying values only.**

Allowed: provider (`google`, `microsoft`, `icloud`, `yahoo`, `custom`), origin (`user`, `initial`, `reconciliation`, `periodic`), folder type, failure category, result, protocol, discovery source, push event type, lock purpose.

Forbidden in metric labels and span attributes: mail account IDs, email addresses, mail/folder/conversation IDs, folder names, IMAP/SMTP hosts or custom domains, subjects, senders, recipients, Message-IDs, attachment names, search text, device tokens, correlation IDs, exception messages, OAuth codes, access or refresh tokens, passwords.

Adding a new instrument or tag must go through `MailClientMetrics` / `MailClientTelemetry` in `MailClient.Application/Observability.cs`, where these rules are reviewed and covered by tests.

## Tracing model

- HTTP server spans come from ASP.NET Core instrumentation. `/health*` and `/metrics` are not traced. `url.path`, `url.query` and `url.full` are removed (paths contain mail/folder GUIDs, queries contain search text); `http.route` keeps the bounded template. Exceptions are not recorded as span events, and request/response bodies are never traced.
- Application spans from ActivitySource `MailClient`: `mailclient.sync.account`, `mailclient.sync.folder`, `mailclient.mail.send`, `mailclient.mail.search`, `mailclient.oauth.refresh`, `mailclient.discovery`.
- Scheduled background sync (periodic, initial, reconciliation, retry) starts its own root `mailclient.sync.account` trace, with one `mailclient.sync.folder` child per folder. There are no per-message spans. Failed spans get `Error` status and `error.type` (exception type name only).

## Logs

- `logs/app-*.json` (application) and `logs/http-*.json` (redacted HTTP bodies) roll daily and on size, keeping the configured number of files. No Elasticsearch or Loki dependency is needed.
- Every event written inside an Activity includes `TraceId` and `SpanId`, so background sync logs correlate with traces. HTTP requests keep `CorrelationId` (the `X-Correlation-ID` header), and authenticated requests keep `MailAccountId`.
- Scheduler logs describe failures by failure category, origin, folder type and attempt, without account or folder GUIDs.
- Unreadable or invalid persisted runtime settings are logged as errors, increment `mailclient.runtime_settings.load_failures`, and fail the operation; they are never silently replaced with defaults.
- `AuditLog` remains the separate business/security audit trail and is not turned into metrics.

## Distributed lock behaviour

`ISyncLockProvider.TryAcquireAsync` returns `Acquired`, `Contended` or `InfrastructureFailure`:

- Sync scheduling: contention requeues immediately. An infrastructure failure is logged, counted as `result="failed"`, and requeued after 30 seconds, so it never becomes a tight loop.
- OAuth refresh: the provider is only called while the distributed lock is held. Contention waits and re-checks for a token refreshed by the holder. An infrastructure failure throws `oauth_refresh_lock_unavailable` (API `503`, sync classifies it as transient) and never refreshes without the lock.
