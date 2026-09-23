# Geliştirme

🇬🇧 [English](../en/development.md)

Production dağıtımı için bkz. [Production](production.md).

## Gereksinimler

.NET 10 SDK, PostgreSQL, Docker (opsiyonel; compose ve GreenMail için).

## Yerelde çalıştırma

Docker ile (Postgres + GreenMail + migration + API, `:8080`):

```bash
cp .env.example .env    # en az Jwt__Key ayarla
docker compose up --build
```

Host portları varsayılan olarak `5432` (Postgres), `3143`/`3025` (GreenMail
IMAP/SMTP), `8080` (API) — bunlar makinenizde zaten çalışan bir şeyle
çakışırsa `.env` içinde `POSTGRES_PORT` / `GREENMAIL_IMAP_PORT` /
`GREENMAIL_SMTP_PORT` / `API_PORT` ile değiştirin.

Docker olmadan: PostgreSQL'i başlatın, migration'ları uygulayın, API'yi
çalıştırın. `dotnet run` `.env` dosyasını okumaz — ayarları gerçek ortam
değişkeni olarak verin (ör. `export ConnectionStrings__Default=...`) ya da
`appsettings.json` varsayılanlarına güvenin (yerel `postgres`/`postgres`,
`mailclient_v2` veritabanı). `dotnet ef` ise `DesignTimeDbContextFactory`'yi
kullanır ve `MAILCLIENT_V2_CONNECTION` değişkenini okur (yoksa aynı yerel
veritabanı, `GSS Encryption Mode=Disable` ile):

```bash
export MAILCLIENT_V2_CONNECTION="Host=localhost;Port=5432;Database=mailclient_v2;Username=postgres;Password=postgres"
dotnet ef database update --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet run --project backend/src/MailClient.Api
```

Çalışma zamanı dosyaları: `data/` (ekler, data-protection anahtarları) ve `logs/`
git tarafından yok sayılır.

### Rider `lan-http` çalıştırma profili

`Properties/launchSettings.json` içinde ayrıca (aynı adlı Rider çalıştırma
konfigürasyonunun kullandığı) bir `lan-http` profili var; API'yi
`0.0.0.0:5071`'de `ASPNETCORE_ENVIRONMENT=Production` ile çalıştırır —
LAN'daki başka bir cihazdan (ör. telefonda Flutter uygulaması) prod-benzeri
davranışa karşı test için: HTTPS-redirect/HSTS mantığı aktif (düz LAN HTTP
üzerinde zararsız — HTTPS portu yapılandırılmadığı için Kestrel bir uyarı
loglar ve isteği yine de sunar), Swagger ve dev CORS kapalı, FCM açık.
Production modu yerleşik development JWT anahtarını reddeder ve profil bilerek
bir anahtar commit'lemez: `Jwt__KeyPath=./data/lan-jwt.key` ayarlar, anahtar bu
dosyadan okunur. Profilin `environmentVariables`'ının işaret ettiği gitignore'lu
üç dosyaya ihtiyaç var; temiz bir clone önce bunları üretmeli/edinmeli:

- `backend/src/MailClient.Api/data/lan-jwt.key` — 32+ rastgele karakter:
  `openssl rand -hex 32 > backend/src/MailClient.Api/data/lan-jwt.key`.
- `backend/src/MailClient.Api/data/lan-dataprotection.pfx` — boş export
  şifreli self-signed bir Data Protection sertifikası:
  `openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 3650 -nodes -subj "/CN=mailclient-lan-dataprotection"`
  ardından `openssl pkcs12 -export -out backend/src/MailClient.Api/data/lan-dataprotection.pfx -inkey key.pem -in cert.pem -passout pass:`.
- `secrets/mail-client-37a01-firebase-adminsdk-fbsvc-588f026e0e.json` —
  projenin Firebase service-account anahtarı (elinde olan birine sorun);
  yoksa gerçek dosyayı edinin ya da no-op push sender'a düşmesi için
  profilde `Firebase__Enabled=false` ayarlayın.

## Firebase Cloud Messaging

Opsiyonel; etkinleştirilmedikçe push bildirimleri no-op olur. Ortam değişkeni
olarak (Docker altında `.env` içinde) ayarlayın: `Firebase__Enabled=true`,
`Firebase__ProjectId=<id>`, `Firebase__CredentialsPath=<service-account JSON yolu>` (Firebase Console →
Project settings → Service accounts'tan alın). Docker altında bunun yerine
`FIREBASE_CREDENTIALS_HOST_PATH` ayarlayın — compose onu bağlar ve
`Firebase__CredentialsPath`'i container içi yola sabitler. Tam kurulum:
[Production → Firebase Cloud Messaging](production.md#5-firebase-cloud-messaging-opsiyonel)
(aynı değişkenler Development'ta da geçerli).

## Build, test, format

```bash
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
dotnet test backend/tests/MailClient.Tests/MailClient.Tests.csproj --filter 'FullyQualifiedName~TestAdi'
```

Uyarılar hata sayılır (`Directory.Build.props`).

## Entegrasyon testleri

Dış servis testleri, açıkça etkinleştirilmedikçe atlanır:

| Senaryo | Etkinleştirme |
|---------|---------------|
| PostgreSQL | `MAILCLIENT_TEST_POSTGRES_ADMIN` (admin bağlantı dizesi) |
| GreenMail (IMAP/SMTP) | `MAILCLIENT_TEST_GREENMAIL=1` veya `MAILCLIENT_TEST_GREENMAIL_*` |

CI (`.github/workflows/ci.yml`) PostgreSQL ve GreenMail çalıştırır; restore,
Release build, NuGet güvenlik denetimi, test ve format kontrolünü yürütür.
CodeQL ve dependency review ayrı çalışır.

## EF Core migration'ları

- `dotnet ef migrations add <Ad>` ile oluşturun; `migrations remove` ile geri alın.
- Migration dosyalarını elle yazmayın, `.Designer.cs` veya model snapshot'ını
  düzenlemeyin. Backfill/özel SQL için `Up()`/`Down()` içini düzenlemek serbesttir.
- `main`'e girmiş migration'ı değiştirmeyin; yenisini ekleyin.
- Docker'da migration'lar API açılışında değil, ayrı `migrate` adımıyla uygulanır.

## Kurallar

- Katmanlamayı koruyun; sağlayıcıya özgü MailKit kodu Infrastructure'da kalır.
- Hesaba ait işlemleri doğrulanmış `MailAccount` ile sınırlı tutun.
- Posta değişiklikleri remote-first kalır ve IMAP UID/UIDVALIDITY'ye uyar.
- Değişikliğin amacı bu değilse API sözleşmelerini ve sabit hata kodlarını koruyun.
- `.env`, kimlik bilgisi, sertifika, anahtar halkası veya `data/` commit etmeyin.
- Conventional commit (`feat:`, `fix:`, `docs:`, `chore:`); yazar yalnızca repo
  sahibidir. Tüm kurallar: [AGENTS.md](../../../AGENTS.md).
