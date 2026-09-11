# Backend Documentation

Canonical, tracked documentation for the `mail-client` ASP.NET Core backend.
These files are the authoritative source; the
[GitHub Wiki](https://github.com/mtsarac/mail-client/wiki) mirrors them.

## English

| Document | Contents |
|---|---|
| [BACKEND_GUIDE.en.md](BACKEND_GUIDE.en.md) | Complete guide: overview, startup, configuration, secrets, auth, accounts, IMAP sync, SMTP sending, attachments, database, background sync, Firebase push, device tokens, concurrency, production, troubleshooting, glossary |
| [ARCHITECTURE.en.md](ARCHITECTURE.en.md) | Project dependencies, DI boundaries, interfaces, lifecycles, diagrams |
| [API_REFERENCE.en.md](API_REFERENCE.en.md) | Every endpoint: method, path, auth, request/response, status codes |
| [DEVELOPMENT.en.md](DEVELOPMENT.en.md) | Setup, running, LAN development, testing, CI, production checklist |
| [SECURITY.en.md](SECURITY.en.md) | All implemented security controls, SSRF, dev-vs-prod rules |

## Türkçe

| Doküman | İçerik |
|---|---|
| [BACKEND_GUIDE.tr.md](BACKEND_GUIDE.tr.md) | Tam kılavuz: genel bakış, başlatma, yapılandırma, sırlar, auth, hesaplar, IMAP senkronu, SMTP gönderimi, ekler, veritabanı, arka plan senkronu, Firebase push, cihaz tokenleri, eşzamanlılık, production, sorun giderme, sözlük |
| [ARCHITECTURE.tr.md](ARCHITECTURE.tr.md) | Proje bağımlılıkları, DI sınırları, arayüzler, yaşam döngüleri, diyagramlar |
| [API_REFERENCE.tr.md](API_REFERENCE.tr.md) | Tüm uç noktalar: metot, yol, auth, istek/yanıt, durum kodları |
| [DEVELOPMENT.tr.md](DEVELOPMENT.tr.md) | Kurulum, çalıştırma, LAN geliştirme, testler, CI, production kontrol listesi |
| [SECURITY.tr.md](SECURITY.tr.md) | Uygulanan tüm güvenlik kontrolleri, SSRF, dev/prod kuralları |

## Conventions

- Code is the source of truth. If these docs ever disagree with
  `backend/src`, the code wins — please fix the docs.
- Secrets are never written here. Placeholders only
  (e.g. `Password=change-me`).
- Ports/paths/config keys used everywhere: `http://localhost:5223`,
  `lan-http` profile, `GOOGLE_APPLICATION_CREDENTIALS`,
  `Firebase__ProjectId`, `Idempotency-Key`.
