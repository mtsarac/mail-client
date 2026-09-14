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

## Migration

```fish
dotnet ef migrations list --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
dotnet ef migrations script --project backend/src/MailClient.Infrastructure --startup-project backend/src/MailClient.Api
```

`legacy-backend/` yalnız referanstır. V2 çalışmasında içeriğini değiştirmeyin veya migration'larını uygulamayın.

## Doğrulama

Commit öncesi warning-as-error build, test ve format çalıştırın; Development OpenAPI'yi inceleyin; yeni `backend/` içinde eski User kavramlarını arayın ve `git diff -- frontend` çıktısının boş olduğunu doğrulayın.
