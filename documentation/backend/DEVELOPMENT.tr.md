# Geliştirme Kılavuzu (Türkçe)

> English: [DEVELOPMENT.en.md](DEVELOPMENT.en.md)

## Önkoşullar

- .NET 10 SDK (`dotnet --version` → 10.x)
- API'yi çalıştırmak için `localhost:5432`'de PostgreSQL
- Docker **yalnızca** entegrasyon testleri için (Testcontainers
  `postgres:16-alpine` + GreenMail çeker); yerel geliştirme Docker
  **gerektirmemelidir**
- `lan-http` profili için Rider (veya VS Code + C#)

## Yapılandırma

```bash
cp backend/src/MailClient.Api/appsettings.Local.example.json \
   backend/src/MailClient.Api/appsettings.Local.json
```

Kopyayı düzenleyin (git'te yok). Asgari çalışan değerler:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=PostaKoprusu;Username=postgres;Password=change-me"
  },
  "Jwt": {
    "Key": "en-az-32-karakterli-yerel-gelistirme-anahtari"
  }
}
```

Veya ortam değişkenleri: `ConnectionStrings__Default`, `Jwt__Key`,
`Firebase__ProjectId`, `GOOGLE_APPLICATION_CREDENTIALS`, …

## Veritabanı

```bash
dotnet ef database update \
  --project backend/src/MailClient.Infrastructure \
  --startup-project backend/src/MailClient.Api
# araç yoksa: dotnet tool install --global dotnet-ef
```

## Çalıştırma

```bash
dotnet run --project backend/src/MailClient.Api/MailClient.Api.csproj
```

- Swagger: `http://localhost:5223/swagger` (yalnızca Development)
- Sağlık: `http://localhost:5223/health`, `http://localhost:5223/health/db`

Tam kalite kapısı (CI'nın çalıştırdığı):

```bash
dotnet restore backend/MailClient.slnx
dotnet build backend/MailClient.slnx --configuration Release --no-restore
dotnet test backend/MailClient.slnx --configuration Release --no-build
dotnet format backend/MailClient.slnx --verify-no-changes
```

`TreatWarningsAsErrors` açık — uyarılar derlemeyi düşürür.

## LAN geliştirme

Backend geliştirici (Rider, bu makine): **`lan-http`** profilini başlatın
(`http://0.0.0.0:5223`, düz HTTP, tarayıcı yok). LAN IP'yi bulun
(`ip -4 addr show | grep inet` — DHCP değiştirir, repoya yazmayın).
API: `http://<LAN-IP>:5223`.

Ekip arkadaşı / cihaz / emülatör / Flutter Web matrisi, CORS/cleartext
kuralları ve güvenlik duvarı sorun giderme için `FLUTTER_HANDOFF.md` ve kök
`README.md` ("Local LAN development") bölümüne bakın. Özeti:

- Dev HTTP→HTTPS yönlendirmez, serbest CORS verir; production tam tersi.
- Yalnızca API LAN'e bağlanır; Postgres localhost'ta kalır.
- Flutter Web, dev CORS sayesinde LAN URL ile çalışır.
- Android LAN URL'leri cleartext'tir: **yalnızca debug** build'de izin verin
  (`networkSecurityConfig`/`usesCleartextTraffic`), release'de asla.
- Localhost çalışıp LAN zaman aşımına uğrarsa: güvenlik duvarı (TCP 5223'ü
  yalnızca güvenilir altağa açın) veya misafir Wi-Fi istemci izolasyonu.

## Firebase modları

- `Firebase:Enabled=false` (varsayılan): push no-op'tur. Burada geliştirin/test edin.
- `Enabled=true`: `ProjectId` + servis hesabı JSON ister
  (`Firebase:CredentialsPath` → `GOOGLE_APPLICATION_CREDENTIALS` → ADC
  bilinen yolu → `GetApplicationDefault()`); yoksa başlatma hızlı hata verir.
  JSON'u asla commit etmeyin; production benzeri kurulumlarda sır olarak
  mount edin.

## Testler

| Proje | Tür | Nasıl |
|---|---|---|
| `MailClient.Api.Tests` | uçtan uca entegrasyon | `WebApplicationFactory` + Testcontainers Postgres (+ posta akışları için GreenMail): auth, izolasyon, token geçersizleme, posta uçları, gönderim, limitler, LAN/yapılandırma, kalıcılık |
| `MailClient.Infrastructure.Tests` | birim | EF InMemory + fake'ler: doğrulama, parola politikası, idempotent gönderim, yeniden yapılandırma, cihaz + push (fake gateway, ağ yok), keşif, eşleme, limitler, depolama, sınıflandırıcı, doğrulayıcı |
| `MailClient.Infrastructure.Tests` | Postgres davranışları | Testcontainers: senkron backlog/bayrak/kullanılabilirlik, Postgres idempotency, migration yükseltme, yarış testleri |

Yerelde yalnızca ihtiyacınızı çalıştırın; Testcontainers kısımları Docker
ister. **Testcontainers'ın doğrulandığı yer CI'dır.** Bu tablonun ötesinde
kapsam iddiasında bulunmayın.

## CI (CD değil — deploy hattı yok)

`master` korumalı: `build-test` + `format` zorunlu kontrollerdir.

### `ci.yml`

- `changes`: `dorny/paths-filter` — backend işleri yalnız
  `backend/**`, `ci.yml` veya `.editorconfig` değiştiyse çalışır (atlama,
  zorunlu kontroller için geçmiş sayılır).
- `build-test`: restore → Release derleme → **NuGet zafiyet denetimi**
  (`--vulnerable --include-transitive`, bulguda hata) → tam test koşumu.
- `format`: `dotnet format --verify-no-changes`.
- Tetikler: `master`'a push, tüm PR'lar. `contents: read`, ref başına
  concurrency + cancel-in-progress.

### `codeql.yml`

Backend yollu push/PR'da C# analizi + **haftalık Pazartesi 06:00**
zamanlı tarama (`0 6 * * 1`). Sonuçlar → Security sekmesi.

## Production kontrol listesi

Kodla zorunlu: güçlü `Jwt:Key`, `Proxy:KnownProxies/KnownNetworks` ile proxy
arkasında HTTPS, Data Protection X509 sertifikası + kalıcı paylaşımlı halka,
sır-mount Firebase kimliği, dev/test dışında `MailSecurity.None` reddi.
Operasyonel ekleyin: DB yedekleri, log/izleme, proxy gövde limiti ≈ 65M,
paylaşımlı ek depolama, sıkı dosya izinleri. Depolama + halka paylaşılmadıkça
tek örnek (advisory lock'lar örnekler arası senkronu zaten koordine eder).
