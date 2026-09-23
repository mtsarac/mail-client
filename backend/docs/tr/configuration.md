# Yapılandırma

🇬🇧 [English](../en/configuration.md)

İki tür ayar vardır:

- **Statik dağıtım ayarları** — ortam değişkenleri / `appsettings*.json`,
  açılışta okunur (bu sayfa).
- **Runtime ayarları** — PostgreSQL'de tek sürümlü bir doküman; management API
  ile canlı değiştirilir (aşağıda).

ASP.NET Core kuralı geçerlidir: JSON'daki `Section:Key`, ortam değişkeni olarak
`Section__Key` yazılır. `.env.example` dosyasını `.env` olarak kopyalayın
(commit etmeyin); `.env`'i yalnızca Docker Compose okur — `dotnet run` onu
yüklemez (bkz. [Geliştirme](development.md#yerelde-çalıştırma)).

## Statik ayarlar

| Anahtar | Amaç | Varsayılan |
|---------|------|------------|
| `ConnectionStrings__Default` | PostgreSQL bağlantı dizesi | yerel Postgres |
| `Jwt__Issuer`, `Jwt__Audience` | JWT issuer/audience | `MailClient` |
| `Jwt__Key` | HMAC anahtarı, **≥ 32 karakter**; yerleşik dev anahtarı Development dışında reddedilir | dev anahtarı |
| `Jwt__AccessTokenMinutes` | Access token ömrü | 15 |
| `Session__RefreshTokenLifetimeDays`, `Session__SlidingExpiration` | Refresh oturumu | 180, true |
| `DataProtection__KeyPath` | Anahtar halkası dizini | `data/protection-keys` |
| `DataProtection__CertificatePath` | Anahtar halkasını koruyan PFX (export şifresiz); **production'da zorunlu** | yok |
| `Proxy__KnownProxies__N`, `Proxy__KnownNetworks__N` | Güvenilen reverse proxy'ler (`X-Forwarded-*`'ı açar); anonim rate limit için kullanılan istemci IP'sini de belirler | yok |
| `Storage__Provider` | `Local` (`<content root>/data/attachments`, Docker'da `/app/data/attachments`) veya `S3` | `Local` |
| `Storage__S3__Bucket/Region/ServiceUrl/Prefix/ForcePathStyle` | S3 hedefi; anahtar çifti boşsa ortamdaki AWS kimlik zinciri kullanılır | — |
| `Storage__S3__AccessKeyId/SecretAccessKey` | Opsiyonel açık S3 kimlik bilgisi | yok |
| `Firebase__Enabled`, `Firebase__ProjectId`, `Firebase__CredentialsPath` | Push bildirimleri (FCM); `Enabled=false`/tanımsız no-op sender kullanır. Kurulum için bkz. [Production](production.md#5-firebase-cloud-messaging-opsiyonel) / [Geliştirme](development.md#firebase-cloud-messaging) | kapalı |
| `OAuth__Google__ClientId/ClientSecret/RedirectUris__N` | Google OAuth; tanımsızsa kapalı | yok |
| `OAuth__Microsoft__ClientId/ClientSecret/Tenant/RedirectUris__N` | Microsoft OAuth | tenant `organizations` |
| `OAuth__StateLifetimeMinutes` | OAuth `state` geçerlilik süresi | 10 |
| `Management__Enabled`, `Management__ApiKey` | Management API (production'da anahtar şart) | kapalı |
| `HttpLogging__*` | Gövde loglama (`Enabled`, `Max*BodyBytes`, `ExcludedPaths`) | açık, 64 KiB |
| `Observability__*`, `OTEL_*` | Trace, metrik, OTLP, Prometheus, log saklama — bkz. [OBSERVABILITY.md](../../OBSERVABILITY.md) | — |
| `MailDiscovery__*` | Posta sunucusu keşif seçenekleri | — |

Yalnızca Docker'a ait değişkenler (`POSTGRES_USER/PASSWORD/DB`,
`DATAPROTECTION_CERT_HOST_PATH`, portlar) [DOCKER.md](../DOCKER.md) içindedir.

Uygulama şu durumlarda açılışta hata verir: production'da bağlantı dizesi, anahtar yolu
veya sertifika yolu eksikse, `Jwt__Key` kısa veya dev anahtarıysa, Management API açık
ama anahtar yoksa.

## Ortamlar

| | Development | Production |
|--|-------------|------------|
| Swagger UI `/swagger` | var | yok |
| CORS | her origin | yok |
| Özel/LAN posta host'ları | serbest | engelli (SSRF) |
| HTTPS yönlendirme | yok | var (TLS'i proxy'de sonlandırıp `Proxy__*` ayarlayın) |
| HSTS | yok | var |
| Kestrel `Server` başlığı | bastırılır | bastırılır |
| Hata `detail` | exception metni | gizli |

Bozuk JSON istek gövdeleri her ortamda `400` ve `invalid_request` koduyla döner.

## Runtime ayarları

`GET/PUT /api/management/runtime-settings` (header `X-Management-Key`). `PUT`
tüm dokümanı değiştirir ve `expectedVersion` taşımalıdır; eski sürüm reddedilir.
Bölümler:

| Bölüm | Kontrol ettiği |
|-------|----------------|
| `Providers` | Sağlayıcı başına (Google, Microsoft, iCloud, Yahoo, Custom): açık/kapalı, yeni/mevcut hesap, şifre / uygulama şifresi / OAuth2 |
| `Sync` | Açık/kapalı, yoklama aralığı (30 sn), flag sync (120 sn), çalıştırma başına en fazla posta (100), hesap/host başına eşzamanlılık, kuyruk kapasitesi, retry sayısı/gecikmeleri, hata eşiği |
| `Limits` | En büyük ek (25 MiB), posta başına ekler (50 MiB), posta (100 MiB), gönderim gövde karakteri |
| `Search` | En büyük sayfa boyutu (100), en uzun sorgu (200) |
| `Push` | Ana anahtar ve olay bazlı anahtarlar, posta önizlemesi |
| `Whitelist` | Allowlist zorunluluğu (kapalı), reconciliation aralığı (15 dk), veri saklama süresi (30 gün) |
