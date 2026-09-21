# Mimari

🇬🇧 [English](../en/architecture.md)

## Katmanlar

```
Api ──► Infrastructure ──► Application ──► Domain
 └────────────────────────► Application
```

| Proje | Sorumluluk |
|-------|------------|
| `MailClient.Domain` | Entity'ler (`MailAccount`, `MailSession`, `MailFolder`, `Mail`, `Conversation`, `Attachment`, `SyncState`, `SendOperation`, `DeviceToken`, `AuditLog`, `AllowlistedEmail`, …) ve enum'lar. Bağımlılığı yok. |
| `MailClient.Application` | İstek/yanıt sözleşmeleri, options, servis arayüzleri, runtime ayar modeli, sync kuyruğu ve retry politikası. |
| `MailClient.Infrastructure` | EF Core (`AppDbContext`, migration'lar), MailKit IMAP/SMTP, keşif, OAuth, push (Firebase), ek depolama (yerel/S3), sync coordinator, tüm servis implementasyonları. |
| `MailClient.Api` | `Program.cs`, middleware, endpoint grupları (`Endpoints/`), JWT, telemetri, Swagger. |

Kural: sağlayıcıya özgü MailKit/IMAP/SMTP kodu Infrastructure'da kalır; endpoint'ler
ince tutulur ve servislere delege eder.

## İstek hattı

`Program.cs` içindeki middleware sırası:

1. `CorrelationMiddleware` — `X-Correlation-ID` okur/üretir
2. `HttpBodyLoggingMiddleware` — maskelenmiş istek/yanıt gövdesi logu
3. Exception handler — hataları sabit hata kodlarına çevirir (RFC 7807 problem details)
4. Forwarded headers (yalnızca `Proxy:*` tanımlıysa)
5. HTTPS yönlendirme (Development dışı)
6. CORS (yalnızca Development)
7. Authentication (JWT bearer) → log zenginleştirme
8. Rate limiter — hesap başına dakikada 60 istek (anonimde IP başına)
9. Authorization → endpoint'ler

## Hesap kapsamı

`MailAccount` güvenlik kapsamıdır. JWT `sub` claim'i hesap kimliğidir;
`ICurrentMailAccount` bunu verir ve hesaba ait her sorgu buna göre filtrelenir.

## Posta akışı

- **Okuma:** arka plandaki `SyncCoordinator` her hesabın klasörlerini IMAP ile
  yoklar (aralık, eşzamanlılık ve retry limitleri runtime ayarlarından gelir);
  posta, konuşma ve ekler PostgreSQL'e yazılır. Endpoint'ler yerel kopyayı okur.
  `POST /api/folders/{id}/sync` anında sync kuyruğa alır (202).
- **Yazma:** değişiklikler (okundu, yıldız, taşı, çöpe…) *remote-first*'tür:
  IMAP sunucusunda UID/UIDVALIDITY ile uygulanır, sonra yerele yansıtılır.
  Reconciliation servisi sapmaları düzeltir.
- **Gönderim:** taslak ve doğrudan gönderim SMTP'den geçer; `Idempotency-Key`
  tekrar denemeleri güvenli yapar (`SendOperation`).
- **Push:** yeni posta, durum değişikliği, yeniden kimlik doğrulama ve sync
  hatası için Firebase bildirimleri (her biri runtime ayarıyla açılıp kapanır).

## Güvenlik önlemleri

- Posta kimlik bilgileri ASP.NET Data Protection ile şifrelenir (`CredentialProtector`).
- Keşif ve dış bağlantılarda SSRF koruması (`OutboundHostValidator`,
  `SsrfSafeDiscoveryHttpHandler`); özel ağ adresleri yalnızca Development'ta serbest.
- Log maskeleme (`LogRedactor`); metrik etiketlerinde adres veya posta ID'si yok.
- HTML posta içeriği temizlenerek işlenir; ekler `IAttachmentStorage` ile saklanır.
- İsteğe bağlı e-posta allowlist'i ([Kimlik doğrulama](authentication.md)).

## Runtime ayarları

Operasyonel ayarlar (sağlayıcı politikası, sync, limitler, arama, push, allowlist)
veritabanında tek sürümlü bir doküman olarak tutulur ve management API ile
optimistic concurrency ile değiştirilir. Statik dağıtım ayarları (bağlantı
dizesi, JWT anahtarı, depolama, observability) ortam değişkenleri/`appsettings`
içinde kalır. Bkz. [Yapılandırma](configuration.md).

## Gözlemlenebilirlik

Serilog JSON logları (`logs/app-*.json`, `logs/http-*.json`), OpenTelemetry
trace/metrik, isteğe bağlı Prometheus `/metrics`. Ayrıntı:
[OBSERVABILITY.md](../../OBSERVABILITY.md).
