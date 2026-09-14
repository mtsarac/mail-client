# Backend Geliştirme (Türkçe)

## Gereksinimler

- .NET 10 SDK
- Runtime ve migration entegrasyonu için PostgreSQL

## Komutlar

```fish
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
```

API çalıştırma:

```fish
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

`ConnectionStrings__Default` ve `Jwt__Key` için ortama özel secret kullanın. Varsayılan development DB adı `mailclient_v2` olur; V2'yi yanlışlıkla legacy DB'ye yöneltmeyin.

## Entegrasyon testleri

PostgreSQL ve GreenMail entegrasyon testleri isteğe bağlıdır; böylece düz `dotnet test` yerel servis kimlik bilgilerini tahmin etmez. Açıkça etkinleştirin:

```fish
set -x MAILCLIENT_TEST_POSTGRES_ADMIN "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=change-me"
set -x MAILCLIENT_TEST_GREENMAIL 1
dotnet test backend/MailClient.slnx --configuration Release --no-build
```

- `MAILCLIENT_TEST_POSTGRES_ADMIN` — yönetici DSN'i. Fixture `mailclient_v2_tests` veritabanını oluşturur ve migrate eder; `mailclient_v2` veritabanına asla dokunmaz.
- `MAILCLIENT_TEST_GREENMAIL=1` — GreenMail testlerini etkinleştirir. Varsayılan container dışında `MAILCLIENT_TEST_GREENMAIL_HOST`, `..._SMTP` (3025), `..._IMAP` (3143), `..._USER` (`test@localhost`), `..._PASSWORD` değerlerini geçersiz kılın.
- `MAILCLIENT_SKIP_INTEGRATION=1` — yukarıdaki değişkenler ayarlı olsa bile entegrasyon testlerini atlar.

GreenMail entegrasyon testleri gerçek IMAP senkron çekirdeğini (UID eşleme, seen bayrakları, UIDVALIDITY), üretim `MailKitRemoteMailFolder.AppendAsync` yolunu ve üretim bağlantı yardımcısının düz metin loopback uç noktasını reddettiğini doğrular. PostgreSQL entegrasyon testleri benzersiz indeks uygulamasını, eşzamanlılık altında atomik refresh-token rotasyonunu ve advisory-lock'lu gönderim idempotency'sini doğrular.

## Migration

```fish
dotnet ef migrations list --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet ef migrations script --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
```

`legacy-backend/` yalnız referanstır. V2 çalışmasında içeriğini değiştirmeyin veya migration'larını uygulamayın.

## Doğrulama

Commit öncesi warning-as-error build, test ve format çalıştırın; Development OpenAPI'yi inceleyin; yeni `backend/` içinde eski User kavramlarını arayın ve `git diff -- frontend` çıktısının boş olduğunu doğrulayın.
