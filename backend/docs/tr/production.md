# Production

🇬🇧 [English](../en/production.md)

`docker-compose.prod.yml` ile tek-host, tek-instance dağıtım: Postgres + tek
seferlik `migrate` adımı + API. GreenMail yok. TLS sonlandırma yok — bu bir
reverse proxy'nin işi. Yerel geliştirme için bkz.
[Geliştirme](development.md).

## 1. Ön koşullar

- Deploy host'unda Docker + Docker Compose.
- API'nin önünde TLS sonlandıran bir reverse proxy (Caddy/nginx/Traefik).
  Compose dosyası bunu sizin için yapmaz.
- Bu proxy için bir domain/sertifika.

## 2. Yapılandırma

```bash
cp .env.example .env
```

`.env` içini doldurun:

| Değişken | Not |
|----------|-----|
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | `postgres` container'ı için kullanılır ve `ConnectionStrings__Default` içine interpolate edilir. |
| `Jwt__Key` | 32+ rastgele karakter. Açılış, Development dışında yerleşik dev anahtarını reddeder. |
| `DATAPROTECTION_CERT_HOST_PATH` | Data Protection anahtar halkasını koruyan `.pfx` dosyasının host yolu (compose bunu `DataProtection__CertificatePath` ile sabitlenen `/run/secrets/dataprotection.pfx`'e bağlar). **Boş export şifresiyle** üretin — uygulama bunu `X509CertificateLoader.LoadPkcs12FromFile(path, password: "")` ile yükler: `openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 3650 -nodes -subj "/CN=mailclient-dataprotection"` ardından `openssl pkcs12 -export -out dataprotection.pfx -inkey key.pem -in cert.pem -passout pass:`. **Bu dosyayı yedekleyin** — kaybedilmesi tüm saklanan posta kimlik bilgilerini çözülemez hale getirir; her hesap yeniden kimlik doğrulaması gerektirir. `protection-keys` volume'ü için de aynısı geçerli. |
| `Proxy__KnownProxies__0` / `Proxy__KnownNetworks__0` | Reverse proxy'nin adresi/adresleri; `X-Forwarded-*` başlıklarının güvenilmesini sağlar. Bir reverse proxy arkasındaysanız **zorunludur**: ayarlanmazsa `UseHttpsRedirection`/`UseHsts` her isteği düz HTTP olarak görür (proxy içeride HTTP konuşur) ve sonsuz bir HTTPS yönlendirme döngüsü oluşur. |
| İhtiyacınız olan OAuth / Storage / Observability / Management değerleri | `.env.example` içindeki yorumlara bakın — tam olarak bu dağıtım için yazıldı. Tam referans: [Yapılandırma](configuration.md). |

## 3. Çalıştırma

```bash
docker compose -f docker-compose.prod.yml up --build -d
```

`migrate` bir kez çalışır, bekleyen EF Core migration'larını uygular, `0` ile
çıkar; `api` yalnızca bu başarılı olduktan sonra başlar
(`depends_on: condition: service_completed_successfully`).

Yeni bir imaj çektikten sonra (ör. güncelleme sonrası) migration'ları yeniden
çalıştırmak için:

```bash
docker compose -f docker-compose.prod.yml up --build migrate
docker compose -f docker-compose.prod.yml up --build -d api
```

## 4. Production modunun açtıkları

Compose dosyasındaki `ASPNETCORE_ENVIRONMENT: Production` ile otomatik ayarlanır
(tam tablo için bkz. [Yapılandırma → Ortamlar](configuration.md#ortamlar)):

- `ConnectionStrings__Default`, `DataProtection__KeyPath` veya
  `DataProtection__CertificatePath` eksikse, ya da `Jwt__Key` kısaysa veya dev
  varsayılanıysa açılış hemen hata verir.
- Swagger arayüzü ve açık dev CORS politikası kapatılır.
- Özel/LAN posta host'ları engellenir (SSRF koruması).
- `UseHttpsRedirection` ve `UseHsts` etkinleşir — yalnızca önde bir reverse
  proxy varsa ve `Proxy__KnownProxies`/`Proxy__KnownNetworks` ayarlıysa
  doğru çalışır (yukarıdaki tabloya bakın).
- Kestrel'in `Server` yanıt başlığı bastırılır.
- İşlenmeyen hata yanıtları exception detayını içermez (yalnızca `code` ve
  `correlationId`).

## 5. Firebase Cloud Messaging (opsiyonel)

Yeni posta, posta durumu değişikliği, sync hatası ve yeniden kimlik
doğrulama gerekli bildirimleri FCM üzerinden gider. Bunu tamamen atlamak
için `Firebase__Enabled`'ı tanımsız (veya `false`) bırakın — API no-op
sender'a düşer, geri kalan her şey normal çalışır.

Etkinleştirmek için:

1. Firebase Console → Project settings → Service accounts → **Generate new
   private key**. Bir service-account JSON'u indirir.
2. `.env` içinde ayarlayın:
   - `Firebase__Enabled=true`
   - `Firebase__ProjectId=<proje-id'niz>`
   - `FIREBASE_CREDENTIALS_HOST_PATH=` o JSON dosyasının host yolu (ör.
     `./secrets/firebase-service-account.json`). Compose bunu container'a
     salt-okunur olarak bağlar ve `Firebase__CredentialsPath`'i
     `/run/secrets/firebase-service-account.json` olarak sabitler — Docker
     altında bu anahtarı kendiniz ayarlamayın.
3. Yeniden deploy edin: `docker compose -f docker-compose.prod.yml up --build -d api`.

Açılış, API trafiği kabul etmeden önce dosyayı yapısal olarak doğrular
(geçerli JSON, `service_account` tipi, ayrıştırılabilir RSA anahtarı);
bozuk veya eksik bir dosya push'u sessizce kapatmak yerine net bir hatayla
hemen başarısız olur. Mobil istemciler oturum açtıktan sonra FCM token'ını
`POST /api/devices` ile kaydeder.

## 6. Volume'ler ve yedekleme

Tam tablo (`pgdata`, `protection-keys`, `attachments`, `logs`) ve yedekleme
komutu için bkz. [DOCKER.md → Volumes](../DOCKER.md#volumes).

## 7. Bilinen ödünleşimler

Tek-instance dağıtım için kabul edilmiştir — tam liste için bkz.
[DOCKER.md](../DOCKER.md#known-trade-offs-accepted-for-a-single-instance-deployment)
(migrator bağlantı dizesinin `docker inspect` ile görünürlüğü, henüz CI imaj
build/push'ı olmaması, Data Protection uygulama adı kapsamı).

## İlgili

[Yapılandırma](configuration.md) (tam ortam değişkeni referansı) ·
[Docker imaj yapısı](../DOCKER.md) (Dockerfile aşamaları) ·
[Observability](../../OBSERVABILITY.md) (log, metrik, tracing)
