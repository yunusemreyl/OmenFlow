# OmenFlow - Mimari, Backend Bağlantıları ve Çalışma Rehberi

Bu dokümantasyon, **OmenFlow** projesinin donanım katmanı (EC & WMI), arka plan servisi (Worker) ve kullanıcı arayüzü (WinUI 3 App) arasındaki haberleşme ağını, buton işlevlerini ve tersine mühendislikle çözülmüş HP BIOS mimarisini detaylandırmaktadır.

---

## 1. Sistem Mimarisi ve İletişim Akışı

```
┌────────────────────────────────────────────────────────┐
│               OmenFlow App (WinUI 3 UI)                │
│    (HomePage, PerformancePage, LightingPage vb.)       │
└──────────┬──────────────────────────────────▲──────────┘
           │                                  │
   [Named Pipe: Command]            [Named Pipe: Telemetry]
     (JSON Payload)                   (JSON Telemetry)
           │                                  │
           ▼                                  │
┌────────────────────────────────────────────────────────┐
│             OmenFlow.Worker (Arka Plan)                │
│       (IpcServer, SensorReader, Host Servisler)        │
└──────────┬──────────────────────────────────▲──────────┘
           │                                  │
    [WMI 0x20008 / EC]               [WMI 0x2D / EC 0xD0]
           │                                  │
           ▼                                  │
┌────────────────────────────────────────────────────────┐
│           HP Donanım Katmanı (BIOS & EC)               │
└────────────────────────────────────────────────────────┘
```

OmenFlow, kullanıcı arayüzü ile donanım kontrolünü iki ayrı işlem (process) olarak izole eder:
1. **`OmenFlow.App` (Client)**: Kullanıcı arayüzünü barındırır. `IpcClient` sınıfı üzerinden Named Pipe (`OmenFlow_HardwareCommand` ve `OmenFlow_HardwareTelemetry`) bağlantısı kurar.
2. **`OmenFlow.Worker` (Server)**: Yönetici haklarıyla (Yüksek Ayrıcalıklı) arka planda çalışır. `PawnIOLib` ve `HP WMI` sürücüleriyle doğrudan donanıma dokunur.

---

## 2. Arayüz Butonları ve Backend (IPC) Komut Tablosu

Arayüzdeki butonların arka plana gönderdiği JSON komutları ve Worker tarafındaki karşılıkları aşağıda özetlenmiştir:

| Arayüz Konumu | Kullanıcı İşlevi | IPC Komut Adı | Gönderilen JSON Örneği / Value | Worker (Backend) İşlevi |
| :--- | :--- | :--- | :--- | :--- |
| **HomePage / PerformancePage** | **Auto Fan Modu** | `SetAuto` | `{"Command": "SetAuto", "Value": null}` | `FanControlService.RestoreAutoControlAsync()` çağrılır. Fan yönetimi BIOS'a bırakılır. |
| **HomePage / PerformancePage** | **Max Fan Modu** | `SetMaxFan` | `{"Command": "SetMaxFan", "Value": true}` | `FanControlService.SetMaxFanAsync(true)` çağrılır. Fanlar %100 LUT (55) seviyesine kilitlenir. |
| **PerformancePage** | **OmenFlow Akıllı Fan** | `ApplyCurve` | `{"Command": "ApplyCurve", "Value": {"Target": 2, "Points": [...]}}` | `FanCurveHostedService.ApplyCustomCurveAsync(...)` devreye girer. Sıcaklığa göre dinamik fan hızı ayarlar. |
| **CustomFanWindow** | **Özel Fan Eğrisi** | `ApplyCurve` | `{"Command": "ApplyCurve", "Value": {"Target": 2, "Points": [...]}}` | Kullanıcının tuval (Canvas) üzerinde sürüklediği sıcaklık/hız noktaları dinamik olarak Worker'a işlenir. |
| **HomePage / PerformancePage** | **Sessiz (Quiet) Mod** | `SetThermalProfile` | `{"Command": "SetThermalProfile", "Value": 80}` *(0x50)* | `PerformanceModeService.SetPerformanceModeAsync(ThermalProfile.Quiet)` çağrılır. CPU/GPU güç limitleri kısılır. |
| **HomePage / PerformancePage** | **Varsayılan (Default)** | `SetThermalProfile` | `{"Command": "SetThermalProfile", "Value": 48}` *(0x30)* | `PerformanceModeService.SetPerformanceModeAsync(ThermalProfile.Default)` çağrılır. Dengeli güç yönetimi sağlanır. |
| **HomePage / PerformancePage** | **Performans Modu** | `SetThermalProfile` | `{"Command": "SetThermalProfile", "Value": 49}` *(0x31)* | `PerformanceModeService.SetPerformanceModeAsync(ThermalProfile.Performance)` çağrılır. Tam CPU/GPU gücü kilidi açılır. |
| **PerformancePage** | **GPU MUX: Hybrid / Discrete** | `SetGpuMode` | `{"Command": "SetGpuMode", "Value": 2}` *(1=Discrete, 2=Hybrid)* | `GpuControlService.SetGpuModeAsync(...)` ile WMI BIOS ekran kartı yönlendirme komutu iletilir. |
| **PerformancePage** | **GPU Gücü (Dynamic Boost)** | `SetGpuPower` | `{"Command": "SetGpuPower", "Value": 2}` *(0=Base, 1=Extra, 2=Max)* | `GpuControlService.SetGpuPowerAsync(...)` ile NVIDIA Dynamic Boost TGP seviyesi yapılandırılır. |
| **LightingPage** | **Statik Klavye Aydınlatması** | `SetLighting` | `{"Command": "SetLighting", "Value": {"BacklightOn": true, "ZoneColors": "..."}}` | `KeyboardLightingService.SetLightingAsync(...)` çağrılır. 4-Bölgeli veya Tek-Bölgeli statik RGB renk paketleri BIOS'a iletilir. |
| **LightingPage** | **RGB Efektleri (Dalga, Nefes)**| `SetLightingEffect` | `{"Command": "SetLightingEffect", "Value": {"Effect": "wave", "Speed": 0.5}}` | Worker içerisindeki `RgbEffectEngine` başlatılır. Arka planda saniyede 10-30 kare hızında anlık renk akışı sağlanır. |
| **AdditionalSettingsPage**| **Pil Koruma (Battery Care)** | `SetBatteryCare` | `{"Command": "SetBatteryCare", "Value": true}` | `PowerService.SetBatteryCareModeAsync(...)` çağrılır. Pili %80 seviyesinde limitleyen BIOS bayrağı değiştirilir. |
| **AdditionalSettingsPage**| **USB Şarj (Kapalıyken)** | `SetUsbCharging` | `{"Command": "SetUsbCharging", "Value": true}` | Bilgisayar kapalıyken USB portlarından güç verilmesi ayarını kontrol eder. |

---

## 3. Donanım Seviyesi (EC & WMI) Çalışma Prensibi ve Deşifre Edilen Sırlar

HP Omen ve Victus cihazlarının fan ve güç bellenimleri (firmware) standart ACPI tabloları yerine doğrudan Embedded Controller (EC) ve WMI BIOS metotlarıyla yönetilmektedir. Projede tersine mühendislikle tespit edilen ve uygulanan kritik çözümler şunlardır:

### 3.1. Fan Hız Oranlaması ve `55 LUT` Bellek Sınırı
* **Tespit**: Victus cihazlarında (örn. `8BBE` anakartları) fan tablosu (Look-Up Table - LUT) 0 ile 100 arasında değil, **0 ile 55** arasında dizinlenmiştir. 55'in üzerinde bir fan kademesi yazıldığında bellek taşması meydana gelir ve fanlar agresif/kontrolsüz bir şekilde dalgalanır ("Fan Kudurması").
* **OmenFlow Çözümü**: `ModelCapabilityDatabase` içerisinde Victus sistemleri için `MaxFanLevel = 55` parametresi kilitlenmiştir. Arayüzden %100 fan talebi geldiğinde bu değer `(100 * 55) / 100 = 55` olarak hesaplanır ve donanıma mutlak güvenli sınırlar içerisinde iletilir.

### 3.2. `0x19` Otomatik Mod Döngü İzolasyonu
* **Tespit**: Kullanıcı özel bir fan kademesi (örn. 45) yazdığında, EC çipi arka planda sürekli kendi otomatik sıcaklık döngüsünü işlettiği için fan hızı önce artıp ardından hemen tekrar BIOS tarafından eziliyordu.
* **OmenFlow Çözümü**: `FanControlService.SetFanLevelAsync` içerisinde, özel hız iletilmeden hemen önce WMI `0x19` komutuyla (Manual Mode Lock) BIOS fan kontrol döngüsü uyutulur.

### 3.3. Hafıza Tortusu ve Hız Atlama Koruması (`Transition Hint - 20`)
* **Tespit**: Fan hızı %100 iken doğrudan %90'a düşürüldüğünde, kontrolör mevcut yüksek devir momentini aşamıyor ve fanlar daha da hızlanıyordu. Ancak önce %20 gibi düşük bir değere inildiğinde kontrolör PWM değerlerini doğru sıfırlayabiliyordu.
* **OmenFlow Çözümü**: Yüksek hız kademeleri arasında ani geçiş yapılırken, OmenFlow servisi milisaniyelik bir `Transition Hint (20)` komutu göndererek kontrolörün bellek tortusunu temizler ve ardından yeni hedef hızı uygular.

### 3.4. Gerçek ve Kararlı RPM Okuması (`WMI 0x2D` ve `EC 0xD0-0xD3`)
* **Tespit**: `0xD0 - 0xD3` EC adresleri ham takometre okuması yapar ve anlık dalgalanan filtrelenmemiş hızlar (6116, 6765 RPM) sunar. Ancak OMEN Gaming Hub, arayüzde stabil ve net olan **5800 / 6100 RPM** maksimum limitlerini göstermektedir.
* **OmenFlow Çözümü**: OmenFlow Worker, fan devrini sorgularken **1. öncelik olarak `WMI 0x2D`** (CMD_FAN_GET_LEVEL) komutunu kullanır. Buradan alınan anlık LUT seviyesi (0-55), `(Seviye * MaxDevir) / 55` formülü ile oranlanarak OMEN Gaming Hub kalitesinde kusursuz, sarsılmaz ve mutlak doğru RPM verisine dönüştürülür.

---

## 4. Geliştirici Servisleri ve Kod Doğrulama

OmenFlow projesini test etmek ve donanım kayıtlarını incelemek için aşağıdaki projeler hazırdır:

* **`OmenFlow.TestConsole`**: Terminal üzerinden interaktif olarak `rpm`, `fan 100`, `maxon`, `auto`, `status` ve `read/write` komutlarını çalıştırabileceğiniz donanım test platformu.
* **`OmenFlow.Worker`**: Uygulama arayüzünden (`OmenFlow.App`) gelen talepleri karşılayan ve Named Pipe üzerinden saniyede bir telemetry verisi yayınlayan bellek dostu arka plan servisi.

OmenFlow projesi, donanım seviyesinde %100 güvenli, istikrarlı ve HP V2 (8BBE) standartlarıyla tam uyumlu bir mühendislik çözümüdür.
