# OmenFlow

**OmenFlow**, HP Omen ve Victus dizüstü bilgisayarlar (özellikle 8BBE anakart serisi ve benzeri modeller) için tasarlanmış, doğrudan donanım seviyesinde (Embedded Controller - EC ve WMI BIOS metotları üzerinden) çalışan; hafif, modern ve güvenilir bir donanım yönetim ve aydınlatma kontrol uygulamasıdır.

> [!WARNING]
> **Proje Durumu: Erken Geliştirme Aşaması (Work in Progress)**
> Bu proje donanım katmanı, servis mimarisi ve temel işlevler açısından tam işlevsel olmakla birlikte aktif bir geliştirme aşamasındadır.

---

## 📸 Ekran Görüntüleri

WinUI 3 ile tasarlanan modern kullanıcı arayüzüne ait ekran görüntüleri:

### 1. Performans ve Fan Kontrolü
![Performans Sayfası](screenshots/performance.png)

### 2. GPU MUX Grafik Değiştirici
![Grafik Değiştirici](screenshots/mux.png)

### 3. Klavye Aydınlatması & RGB Efektleri
![RGB Sayfası](screenshots/keyboard.png)

### 4. Ek Güç ve Sistem Ayarları
![Ayarlar Sayfası](screenshots/settings.png)

---

## 🌐 İletişim Mimarisi (Communication Architecture)

OmenFlow, güvenliği ve kararlılığı artırmak adına kullanıcı arayüzü (Arayüz Katmanı) ile donanım kontrolünü (Çekirdek Katman) iki farklı işleme (process) böler:

```
┌────────────────────────────────────────────────────────┐
│               OmenFlow App (WinUI 3 UI)                │
│    (HomePage, PerformancePage, LightingPage, vb.)     │
│                     (Standart Yetki)                   │
└──────────┬──────────────────────────────────▲──────────┘
           │                                  │
  [HTTP POST: /api/command]           [HTTP GET: /api/telemetry]
      (JSON Payload)                    (JSON Telemetry)
           │                                  │
           ▼                                  │
┌────────────────────────────────────────────────────────┐
│             OmenFlow.Worker (Background)               │
│         (HTTP Server, SensorReader, Servisler)         │
│                     (Yönetici Yetkisi)                 │
└──────────┬──────────────────────────────────▲──────────┘
           │                                  │
  [WMI 0x20008 / EC 0x62-0x66]        [WMI 0x2D / EC 0xD0-0xD3]
           │                                  │
           ▼                                  │
┌────────────────────────────────────────────────────────┐
│           HP Donanım Katmanı (BIOS & EC)               │
└────────────────────────────────────────────────────────┘
```

### 1. Client-Server Ayrımı
* **`OmenFlow.App` (İstemci)**: Kullanıcının gördüğü WinUI 3 arayüzüdür. Windows güvenlik ilkeleri gereği **standart kullanıcı yetkileriyle** çalıştırılır. Donanıma doğrudan erişim hakkı yoktur.
* **`OmenFlow.Worker` (Sunucu)**: Arka planda çalışan bir Windows minimal konsol/servis uygulamasıdır. Donanımsal portlara ve WMI BIOS sınıflarına erişebilmesi için **Yönetici (Elevated - Administrator)** yetkileriyle koşturulur.

### 2. Yerel HTTP API İletişimi
İstemci ve sunucu arasındaki tüm haberleşme, yerel döngü (loopback) üzerinde **`http://localhost:50312`** portu üzerinden gerçekleşir.
* **Telemetri Hattı (GET `/api/telemetry`)**: Arayüzdeki [IpcClient.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.App/Helpers/IpcClient.cs), arka plan servisini her **2 saniyede bir** sorgulayarak sıcaklıklar, RPM değerleri, anlık güç planı ve RGB durumlarını içeren tek bir JSON nesnesi alır.
  * *Adaptif Geri Çekilme (Adaptive Polling)*: Sunucuya ulaşılamadığında sorgulama aralığı kademeli olarak artırılarak (5s -> 10s -> 20s -> maksimum 30s) gereksiz CPU yükü engellenir.
* **Komut Hattı (POST `/api/command`)**: Arayüzdeki eylemler (fan hızı ayarlama, renk değiştirme vb.) sunucuya JSON gövdesiyle gönderilir (örn: `{"Command": "SetBatteryCare", "Value": true}`).

### 3. Otomatik Servis Tetikleme
* İstemci açıldığında işletim sisteminde `OmenFlow.Worker` işleminin çalışıp çalışmadığını kontrol eder.
* Süreç aktif değilse, `runas` (yönetici olarak çalıştır) fiili çağrılarak Windows UAC onay penceresi tetiklenir ve `OmenFlow.Worker.exe` arka planda gizli bir pencerede başlatılır.

---

## 📊 Veri Kaynakları: Neyi Nereden Alıyoruz?

| Telemetri / Veri Tipi | Birincil Kaynak | İkincil / Yedek Kaynak | İşleme / Dönüştürme Yöntemi |
| :--- | :--- | :--- | :--- |
| **CPU / GPU Sıcaklığı** | WMI CMD `0x23` | LHM (`SensorReader`) | WMI üzerinden alınan 1-byte veri doğrudan Santigrat derece olarak okunur. |
| **Fan RPM (Hız Dönüşü)**| WMI CMD `0x38` | EC Registers (`0xD0-0xD3`) | WMI `0x38` yoksa EC `0xD0-0xD3` portlarından 16-bitlik veri birleştirilir: `(HighByte << 8) \| LowByte`. |
| **Fan LUT Kademe Oranı**| WMI CMD `0x2D` | WMI CMD `0x37` | Victus gibi cihazlarda gerçek RPM okunamadığında, 0-55 arası LUT değeri `(Kademe * MaxRPM) / 55` formülü ile RPM'e simüle edilir. |
| **CPU / GPU Yükü & Güç** | LibreHardwareMonitor | Yok | [SensorReader.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.Worker/SensorReader.cs) aracılığıyla LHM donanım ağacı taranarak işlemci çekirdek yükü ve Package Power (W) değerleri anlık çekilir. |
| **GPU MUX Modu** | Windows Registry | WMI `Win32_VideoController` | Registry'deki `InternalMuxState` değeri okunur (`2` = Discrete, `1` = Hybrid). Alternatif olarak dahili grafik kartının (iGPU) sistemdeki aktifliği kontrol edilir. |
| **GPU Güç Sınırları** | WMI CMD `0x21` | `nvidia-smi` aracı | WMI `0x21` ile customTgp, PPAB ve Dynamic Boost yavaşlama sıcaklığı (`peakTemp`) bilgileri okunur. |
| **Klavye Aydınlatması** | WMI CMD `0x20009` | WMI CMD `0x20008` (0x2B) | Renk tablosu `0x20009/0x02` üzerinden 128 baytlık bellek alanından (25. - 36. byte aralığı) 4-Bölge RGB renk kodları olarak ayrıştırılır. |
| **Batarya Koruma** | WMI CMD `0x24` | Yok | Pil şarj sınırı modu (%80 limiti) BIOS'tan 1-byte bayrak olarak sorgulanır. |

---

## 🛡️ Güvenlik ve Yardımcı Arka Plan Servisleri

OmenFlow, kararsızlığı önlemek ve donanımı korumak amacıyla arka planda çalışan zengin yardımcı servislere sahiptir:

### 1. Sessiz Mod Termal Koruyucu ([QuietSafetyMonitor.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.Hardware/QuietSafetyMonitor.cs))
* **Sorun**: Sessiz modda (Quiet) BIOS, fanları ve CPU gücünü aşırı derecede kısar. Ağır yük altında (kod derleme, video render) işlemci sıcaklığı hızla 95°C üzerine fırlayabilir.
* **Çözüm**: Isı izleme servisi Quiet moddayken CPU sıcaklığını sürekli izler. Sıcaklık **93.0°C** eşiğini geçer ve bu eşikte kesintisiz **8 saniye** boyunca kalırsa, profil otomatik olarak **Default (Dengeli)** moduna yükseltilir.
* **Güvenlik**: Güvenliğe geçiş yapıldığında profil otomatik olarak sessiz moda geri dönmez; kullanıcının bunu manuel olarak yapması gerekir. İki koruma tetiklemesi arasında **5 dakikalık** soğuma süresi (cooldown) uygulanır.

### 2. Fan Hızı Doğrulama Servisi ([FanVerificationService.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.Hardware/FanVerificationService.cs))
* **İşlev**: Donanıma gönderilen fan komutlarının fiziksel olarak uygulanıp uygulanmadığını kontrol eder.
* **Çalışma Prensibi**: Bir fan hızı veya profili ayarlandıktan **1.8 saniye** sonra (profil geçişlerinde **3.6 saniye**) donanım fan RPM'i geri okunur.
* **Karşılaştırma**: Okunan RPM değeri, hedeflenen fan yüzdesine tekabül eden beklenen RPM değeri ile karşılaştırılır (tolerans aralığı ±%35 olarak esnek tutulmuştur).
* **Blokaj Tespiti**: Eğer hedef hız %0'dan büyük olmasına rağmen ardışık **3 doğrulama** boyunca fanlardan 0 RPM dönüyorsa, günlük dosyasına "Stuck Fan/EC Comm Issue" (Sıkışmış Fan veya EC İletişim Hatası) uyarısı basılır.

### 3. Dinamik Kalibrasyon Servisi ([FanCalibrationService.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.Hardware/FanCalibrationService.cs))
* **İşlev**: Farklı HP cihaz ailelerinin (Omen V1, Omen V2, Victus, Victus S vb.) karakteristik fan hız/RPM davranışlarını eşleştirir.
* **Öğrenme Mekanizması**: Cihaz yüksek fan hızlarında çalışırken (örn. %90 ve üzeri) anlık okunan en yüksek RPM değerlerini kaydeder.
* **Kayıt**: Öğrenilen gerçek maksimum RPM sınırları ve kalibrasyon noktaları yerel olarak `C:\ProgramData\OmenFlow\fan_calibration.json` dosyasında saklanır.

### 4. Güç Kaynağı Otomasyonu ([PowerAutomationService.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.Hardware/PowerAutomationService.cs))
* **İşlev**: Bilgisayarın prizden çıkarılması veya prize takılması durumlarında güç profilini otomatik olarak yönetir.
* **Kural Seti**: Varsayılan olarak AC (Şarj) bağlıyken **Performance**, Bataryada çalışırken ise **Quiet** profili uygulanır. Ayarlar ekranından özelleştirilebilir ve `C:\ProgramData\OmenFlow\power_automation.json` dosyasına kaydedilir.

### 5. Uyku ve Uyanma Yönetimi ([SuspendRecoveryService.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.Hardware/SuspendRecoveryService.cs))
* **Sorun**: Bilgisayar uyku moduna (Suspend) geçerken fan keep-alive komutlarının gönderilmeye devam etmesi fanların yüksek hızda takılı kalmasına veya cihazın uykudan uyanamamasına (standby arızası) yol açabilir.
* **Askıya Alma**: Uyku sinyali alındığında, fan eğrisi motoru tamamen durdurulur ve keep-alive sayaçları iptal edilir. Aktif durumun (fan modu, profil, aydınlatma) bir anlık görüntüsü (snapshot) belleğe alınır.
* **Geri Yükleme**: Sistem uykudan uyandığında (Resume) **3 saniye** beklenerek BIOS/ACPI katmanının stabil hale gelmesi sağlanır. Ardından uykudan önceki son profil, fan eğrisi ve RGB ışıklandırma değerleri donanıma sırasıyla güvenle yeniden yazılır.

### 6. Tanı ve Teşhis Dışa Aktarımı ([DiagnosticsExportService.cs](file:///c:/Users/yeyil/Documents/GitHub/OmenFlow/OmenFlow.Hardware/DiagnosticsExportService.cs))
* **İşlev**: Hata bildirimi veya inceleme amacıyla sistemin tüm donanımsal register ve geçmiş kaydını tek bir sıkıştırılmış dosya haline getirir.
* **Kayıt Konumu**: Dosya kullanıcının masaüstüne `OmenFlow_Diagnostics_yyyy-MM-dd_HH-mm-ss.zip` adıyla kaydedilir.
* **Paket İçeriği**:
  * `diagnostics.txt`: Genel sistem özeti, sıcaklıklar, RPM'ler ve etkin profiller.
  * `fan_command_history.txt`: Son 80 fan komutunun zamanı, hedef değeri ve başarı durumu.
  * `fan_calibration.txt`: Kalibrasyon geçmişi ve gözlemlenen maksimum RPM sınırları.
  * `ec_snapshot.txt`: Kritik Embedded Controller register adreslerinin anlık değerleri (`0x95`, `0xCE`, `0x34`, `0x35`, `0xD0-D3`, `0x80`, `0xA0`).
  * `event_log.txt`: Son 500 satırlık işlem günlüğü.

---

## ⚙️ WMI & EC Komut Referansı (Detaylı Katalog)

Aşağıda OmenFlow tarafındaki donanım haberleşmesini yöneten alt komutlar parametrik yapılarıyla birlikte açıklanmıştır.

### 1. Donanım Denetim Komutları (WMI Kategori: `0x20008`)

Tüm istekler `root\WMI` altındaki `hpqBDataIn` sınıfının `hpqBIntM` metoduna gönderilir. Girdi nesnesi properties: `Sign` (her zaman `[0x53, 0x45, 0x43, 0x55]`), `Command` (`0x20008`), `CommandType` (Aşağıdaki Sub-CMD değerleri), `hpqBData` (Input payload) ve `Size` (Input payload boyutu).

#### 🔴 `0x10` (Heartbeat / Wake-Up)
* **Payload**: `[0x00, 0x00, 0x00, 0x00]` (4-byte sıfır)
* **OutSize**: 4 Byte
* **Görevi**: BIOS uykudayken WMI portunu dinlemeye başlaması için dürtükler. Ayrıca fan manuel kontroldeyken her 5 saniyede bir gönderilerek BIOS'un otomatik fan kontrolünü zorla geri almasını (override) engeller.

#### 🔴 `0x1A` (Thermal Policy Switch)
* **Payload**: `[0xFF, ModeByte, 0x00, 0x00]`
* **OutSize**: 0 Byte
* **Görevi**: BIOS düzeyinde donanım güç sınırlarını (TDP) ve fan eşiklerini değiştirir.
  * `ModeByte = 0x30` (48) -> Default (Dengeli)
  * `ModeByte = 0x31` (49) -> Performance (Yüksek Güç Limitleri)
  * `ModeByte = 0x50` (80) -> Quiet (Sessiz / Düşük TDP limitleri)

#### 🔴 `0x21` (Get GPU Power State)
* **Payload**: `[0x00, 0x00, 0x00, 0x00]` (4-byte)
* **OutSize**: 4 Byte
* **Görevi**: GPU Dynamic Boost ve TGP durumunu okur.
  * Gelen tamponun `data[0]` değeri `customTgp` (0 veya 1), `data[1]` değeri `PPAB` (0 veya 1), `data[3]` değeri ekran kartı termal yavaşlama eşik sıcaklığıdır (`peakTemp`).

#### 🔴 `0x22` (Set GPU Power State)
* **Payload**: `[CustomTgp, Ppab, 0x01, PeakTemp]`
* **OutSize**: 0 Byte
* **Görevi**: NVIDIA Dynamic Boost TGP ek sınırlarını yönetir.
  * Base Power için `CustomTgp=0, Ppab=0`, Extra Power için `CustomTgp=1, Ppab=0`, Max Power (maksimum boost) için `CustomTgp=1, Ppab=1` gönderilir. `PeakTemp` değeri yazılırken donanım koruması adına öncelikle `0x21` ile okunan orijinal değer korunarak aynen iletilir (asla sıfır yazılmaz).

#### 🔴 `0x23` (Get Temperature Sensors)
* **Payload**: `[SensorID, 0x00, 0x00, 0x00]`
* **OutSize**: 4 Byte
* **Görevi**: Anakart üzerindeki fiziksel ısı sensörlerini okur.
  * `SensorID = 0x01` -> CPU Sıcaklığı. Çıktı `OutData[0]` değerindedir.
  * `SensorID = 0x02` -> GPU Sıcaklığı. Çıktı `OutData[0]` değerindedir.

#### 🔴 `0x24` (Battery Care Mode)
* **Payload**: `[ModeByte, 0x00, 0x00, 0x00]`
* **OutSize**: 4 Byte (Okurken) / 0 Byte (Yazarken)
* **Görevi**: Bataryanın %80 şarj limit korumasını açar veya kapatır.
  * `ModeByte = 0x01` -> Şarjı %80'de keserek pil ömrünü korur.
  * `ModeByte = 0x00` -> Pili %100'e kadar şarj eder.

#### 🔴 `0x27` (Set Max Fan Command)
* **Payload**: `[EnabledByte, 0x00, 0x00, 0x00]`
* **OutSize**: 0 Byte
* **Görevi**: Fanları maksimum hıza kilitler.
  * `EnabledByte = 0x01` -> Fanlar %100 duty seviyesine kilitlenir.
  * `EnabledByte = 0x00` -> Kilit açılır ve fan kontrolü tekrar WMI profil kurallarına bırakılır.

#### 🔴 `0x29` (Set CPU Power Limits)
* **Payload**: `[PL1_Watt, PL2_Watt, 0x00, 0x00]`
* **OutSize**: 0 Byte
* **Görevi**: İşlemcinin sürekli güç limitini (PL1) ve kısa süreli turbo güç limitini (PL2) doğrudan Watt birimiyle ayarlar (Örn: 45W / 90W limitleri için `[45, 90, 0, 0]`).

#### 🔴 `0x2B` (Get Keyboard Feature Type)
* **Payload**: `[0x00, 0x00, 0x00, 0x00]`
* **OutSize**: 4 Byte
* **Görevi**: Cihazdaki fiziksel klavye backlight yeteneğini sorgular.
  * Dönüş değeri `OutData[0] == 0x04` ise 4 Bölgeli RGB klavye, `0x05` ise Per-Key RGB, diğer değerlerde ise Standart 1-Bölge aydınlatma olarak tanımlanır.

#### 🔴 `0x2D` / `0x37` (Get Fan Level LUT V1 / V2)
* **Payload**: `[0x00, 0x00, 0x00, 0x00]`
* **OutSize**: 128 Byte
* **Görevi**: Fanların BIOS tarafından belirlenen anlık çalışma kademe indeksini döner. V1/Victus modellerinde 0-55 aralığındadır. V2 modellerinde ise 0-100 aralığındadır.
  * `OutData[0]` = CPU fan seviyesi, `OutData[1]` = GPU fan seviyesi.

#### 🔴 `0x2E` (Set Fan Level LUT)
* **Payload**: `[CpuLevel, GpuLevel, 0x00, 0x00]`
* **OutSize**: 0 Byte
* **Görevi**: Fanların hızını elle belirler. Victus/V1 cihazlarında `CpuLevel` ve `GpuLevel` parametreleri maksimum **55** olacak şekilde sınırlandırılır.

#### 🔴 `0x38` (Get Fan RPM - V2)
* **Payload**: `[0x00, 0x00, 0x00, 0x00]`
* **OutSize**: 128 Byte
* **Görevi**: Yeni nesil (Omen V2) cihazlarda fanların anlık gerçek dönme hızlarını (RPM) doğrudan BIOS takometresinden milisaniyelik gecikme olmadan çeker.
  * CPU RPM: `OutData[0] \| (OutData[1] << 8)`
  * GPU RPM: `OutData[2] \| (OutData[3] << 8)`

---

### 2. Aydınlatma Denetim Komutları (WMI Kategori: `0x20009`)

#### 🔵 `0x02` (Get RGB Lighting Table)
* **Payload**: `[0x00, 0x00, ... 0x00]` (128 byte)
* **OutSize**: 128 Byte
* **Görevi**: 4-Bölge RGB renk tablosunun o anki durumunu okur. Renk kodları çıktının `25` ile `36` indeksleri arasındaki 12-byte bellek alanındadır.

#### 🔵 `0x03` (Set RGB Lighting Table)
* **Payload**: `[0x03, 0x00, ... [Zones] ...]` (128 byte)
* **OutSize**: 0 Byte
* **Görevi**: 4-Bölge RGB klavyeye renk paketi gönderir.
  * İlk byte `0x03` olmalıdır. Bölge renkleri 25. bayttan başlar:
    * Zone 1 (R, G, B) -> `payload[25], payload[26], payload[27]`
    * Zone 2 (R, G, B) -> `payload[28], payload[29], payload[30]`
    * Zone 3 (R, G, B) -> `payload[31], payload[32], payload[33]`
    * Zone 4 (R, G, B) -> `payload[34], payload[35], payload[36]`

#### 🔵 `0x04` / `0x05` (Get/Set Standard Backlight)
* **Görevi**: Tek renk aydınlatmalı klavyelerin ışığını açar veya kapatır.
  * `0x04` sorgusunda dönüş değeri `0xE4` ise açık, `0x64` ise kapalıdır.
  * `0x05` yazma komutunda payload `[0xE4, 0x00, 0x00, 0x00]` (Açık) veya `[0x64, 0x00, 0x00, 0x00]` (Kapalı) olarak gönderilir.

---

### 3. Ekran Kartı MUX Switch Komutları (WMI Kategori: `0x00002` / `0x00001` Fallback)

#### 🟢 `0x52` (MUX Switch Mode)
* **Payload**: `[ModeByte, 0x00, 0x00, 0x00]`
* **OutSize**: 4 Byte (Okurken) / 0 Byte (Yazarken)
* **Görevi**: Ekran kartı MUX anahtarını değiştirir.
  * `ModeByte = 0x01` -> Yalnızca Harici GPU (Discrete Mode - Intel/AMD ekran kartı devre dışı kalır, maksimum oyun performansı).
  * `ModeByte = 0x00` -> Hibrit / Optimus (Hybrid Mode - Enerji tasarrufu).
  * *MUX değişimi gönderildikten sonra WMI istekleri askıya alınır, bilgisayar yeniden başlayana kadar donanım korumaya alınır.*

---

### 4. Embedded Controller (EC) Register Komut Kataloğu

OmenFlow, düşük seviyeli doğrudan donanım komutlarını Embedded Controller (EC) register'larına yazarak fallback sağlar. EC portları `0x62` (Data) ve `0x66` (Command) üzerinden PawnIO arayüzüyle kontrol edilir.

* **`0x34` (CPU Fan Override Level)**: CPU fan hız oranını (LUT seviyesi olarak) doğrudan EC'ye yazar.
* **`0x35` (GPU Fan Override Level)**: GPU fan hız oranını (LUT seviyesi olarak) doğrudan EC'ye yazar.
* **`0x95` (Hardware Profile Target)**: WMI 0x1A komutunun donanımsal karşılığıdır. `0x01` Performance, `0x02` Quiet, `0x00` Default (Dengeli) modunu temsil eder.
* **`0xCE` (Fan Profile Transition Trigger)**: Victus S serilerinde fan kademesi geçişlerinde BIOS'un fanları aniden durdurmasını engellemek için geçiş tetiği sağlar (`0x00` = Quiet, `0x01` = Default, `0x02` = Performance).
* **`0xD0-0xD1` (CPU Fan Tachometer Lo/Hi)**: CPU fanının o anki anlık dönüş devrini (RPM) okur.
* **`0xD2-0xD3` (GPU Fan Tachometer Lo/Hi)**: GPU fanının o anki anlık dönüş devrini (RPM) okur.

---

## 📂 Dizin Yapısı ve Dosyaların Görevleri

Proje dosyaları mantıksal olarak 5 ana bölüme ayrılmıştır:

```
OmenFlow/
│
├── OmenFlow.Core/                               # Paylaşılan model dosyaları ve arayüz sözleşmeleri
│   ├── Models/                                  # Telemetri, fan eğrileri ve güç planı DTO'ları
│   ├── Interfaces/                              # Donanım servis soyutlamaları
│   └── Services/                                # Logger.cs (Merkezi günlük kaydı)
│
├── OmenFlow.Hardware/                           # Donanım erişim servisleri ve WMI/EC kontrolcüleri
│   ├── BiosService.cs                           # WMI kuyruk döngüsü ve heartbeat servisi
│   ├── EcService.cs                             # PawnIO ile donanımsal register okuma/yazma
│   ├── ModelCapabilityDatabase.cs               # Victus (55 LUT) / Omen (100 LUT) model sınırları veritabanı
│   ├── FanControlService.cs                     # Fan modları ve otomatik/manuel fan kontrolü
│   ├── FanCurveHostedService.cs                 # Kullanıcı fan eğrilerini uygulayan arka plan iş parçacığı
│   ├── QuietSafetyMonitor.cs                    # Sessiz mod aşırı ısınma koruması
│   ├── SuspendRecoveryService.cs                # Uykudan uyanma sonrası durum geri yükleyici
│   ├── DiagnosticsExportService.cs              # Masaüstüne tanı ZIP paketi oluşturan servis
│   ├── FanCalibrationService.cs                 # Model tabanlı RPM kalibrasyon yöneticisi
│   └── FanVerificationService.cs                # RPM hızı doğrulama ve stuck fan kontrolü
│
├── OmenFlow.Worker/                             # Yönetici yetkili arka plan sunucusu
│   ├── Program.cs                               # Minimal HTTP API (localhost:50312) ve yönlendirmeler
│   ├── SensorReader.cs                          # LHM ile CPU/GPU yük, güç ve RAM takibi
│   └── WmiBiosMonitor.cs                        # Arka planda telemetri tazeleyen periyodik işçi
│
├── OmenFlow.App/                                # WinUI 3 tabanlı kullanıcı arayüzü (İstemci)
│   ├── Pages/                                   # Arayüz sayfaları (Performans, Aydınlatma, MUX vb.)
│   └── Helpers/IpcClient.cs                     # HTTP sunucusu ile haberleşen istemci sınıfı
│
├── OmenFlow.TestConsole/                        # Geliştiriciler için interaktif WMI/EC terminali
└── README.md                                    # Ana dökümantasyon dosyası
```

---

## 🤝 Teşekkürler ve Katkıda Bulunanlar

OmenFlow, HP donanım katmanlarının tersine mühendislik çalışmaları ve açık kaynaklı araştırmalar üzerine inşa edilmiştir. Aşağıdaki projelere sundukları altyapı ve paylaştıkları kıymetli bilgiler için teşekkür ederiz:
* **[omencore](https://github.com/theantipopau/omencore)**: HP WMI ve EC katmanlarının çözümlenmesinde öncülük eden ve WMI protokollerinin anlaşılmasını sağlayan eşsiz bir tersine mühendislik projesi.
* **[OmenMon-Reborn](https://github.com/seakyy/OmenMon-Reborn)**: EC fan kontrol sınırları, bellek taşmaları (55 LUT kısıtlaması) ve kararlı fan eğrileri konusunda pratik çözümler sunan başarılı bir çalışma.

---

## 📄 Lisans

Bu proje açık kaynak topluluğuyla paylaşılmak üzere geliştirilmiştir. Ayrıntılar için `LICENSE` dosyasına göz atabilirsiniz.
