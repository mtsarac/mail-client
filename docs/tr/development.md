# Geliştirme

🇬🇧 [English](../en/development.md)

## Gereksinimler

.NET 10 SDK, PostgreSQL, Docker (opsiyonel; compose ve GreenMail için).

## Yerelde çalıştırma

Docker ile (Postgres + GreenMail + migration + API, `:8080`):

```bash
cp .env.example .env    # en az Jwt__Key ayarla
docker compose up --build
```

Docker olmadan: PostgreSQL'i başlatın, `.env` içinde `ConnectionStrings__Default`
ayarlayın, migration'ları uygulayın, API'yi çalıştırın:

```bash
dotnet ef database update --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet run --project backend/src/MailClient.Api
```

Çalışma zamanı dosyaları: `data/` (ekler, data-protection anahtarları) ve `logs/`
git tarafından yok sayılır.

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
- `master`'a girmiş migration'ı değiştirmeyin; yenisini ekleyin.
- Docker'da migration'lar API açılışında değil, ayrı `migrate` adımıyla uygulanır.

## Kurallar

- Katmanlamayı koruyun; sağlayıcıya özgü MailKit kodu Infrastructure'da kalır.
- Hesaba ait işlemleri doğrulanmış `MailAccount` ile sınırlı tutun.
- Posta değişiklikleri remote-first kalır ve IMAP UID/UIDVALIDITY'ye uyar.
- Değişikliğin amacı bu değilse API sözleşmelerini ve sabit hata kodlarını koruyun.
- `.env`, kimlik bilgisi, sertifika, anahtar halkası veya `data/` commit etmeyin.
- Conventional commit (`feat:`, `fix:`, `docs:`, `chore:`); yazar yalnızca repo
  sahibidir. Tüm kurallar: [AGENTS.md](../../AGENTS.md).
