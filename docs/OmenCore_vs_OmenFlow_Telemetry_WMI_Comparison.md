# OmenCore vs OmenFlow

Bu belge, referans olarak kullanılan [omencore-main](C:/Users/yeyil/Desktop/omencore-main) ile mevcut OmenFlow kod tabanını telemetri ve WMI/EC komut akışları açısından karşılaştırır.

Kapsam:
- Fan ve sıcaklık telemetrisi
- Performans profili ve fan modu komutları
- GPU MUX / GPU güç komutları
- Davranış farkları ve eksik kalan güvenlik katmanları

## 1. Mimari Farkı

OmenCore daha çok tek bir çekirdek servis seti etrafında şekillenir. Fan, performans ve telemetri aynı domain içinde, sürekli çalışan kontrol döngüleriyle yönetilir.

OmenFlow ise UI ile donanım kontrolünü ayırır:
- `OmenFlow.App`: WinUI 3 arayüzü
- `OmenFlow.Worker`: WMI/EC komutlarını yürüten arka plan servis
- `OmenFlow.Hardware`: HP BIOS, EC ve fan mantığı

Bu ayrım OmenFlow’u daha temiz ve minimal bir kontrol köşesine uygun hale getirir, fakat bazı OmenCore güvenlik davranışları ayrıca taşınmadığı sürece kaybolabilir.

## 2. Telemetri Karşılaştırması

### OmenCore yaklaşımı

OmenCore telemetriyi salt "okuma" olarak görmez; durum makinesi gibi işler:
- Sürekli fan eğrisi takibi
- Adaptive polling
- Fan RPM değişimini doğrulama
- Geçici 0 RPM durumunda eski değeri koruma
- RPM okunamazsa kullanıcıya yanlış veri göstermeme
- Suspend / resume ve geçiş dönemleri için özel koruma

Referans dosyada bu davranışlar özellikle `FanService` içinde toplanmış durumda. Fan telemetrisi sadece sensör değeri değil, aynı zamanda kararlılık ve durum bilgisidir.

### OmenFlow yaklaşımı

OmenFlow’da telemetri şu akışla toplanıyor:
- Worker içinde `SensorReader` LibreHardwareMonitor ile CPU/GPU sıcaklık, yük ve güç değerlerini okuyor
- Fan RPM önce WMI `0x38`, sonra EC `0xD0-0xD3`, sonra WMI `0x2D` ve `0x37` ile okunuyor
- UI bu veriyi `TelemetryReceived` ile alıp kartlara basıyor

Güçlü taraflar:
- CPU ve GPU telemetrisi mevcut
- Fan RPM için çoklu fallback var
- Victus tarafı için `0x2D` tabanlı LUT ölçekleme yapılmış

Eksik taraflar:
- RPM için "unavailable / stale" ayrımı yok
- Geçici 0 RPM ile gerçek fan stop aynı şekilde gösteriliyor
- OmenCore’daki geçiş penceresi ve doğrulama mantığı daha zengin

### Net sonuç

OmenCore telemetriyi güvenilirlik katmanlarıyla birlikte sunuyor. OmenFlow ise doğru veri toplama tarafında iyi, fakat kullanıcıya gösterim katmanında daha sade ve daha ham kalıyor.

## 3. Fan / Performans Komutları

### OmenCore

OmenCore’da fan ve performans davranışı daha ayrıntılı biçimde ayrıştırılmış:
- Fan eğrileri sürekli uygulanıyor
- Eğri ile sistem profil ilişkisi varsayılan olarak gevşek tutuluyor
- Performans modu değişince fan politikası otomatik olarak zorlanmıyor
- GPU güç limiti ve performans profili uygulaması model kapasitesine göre sınırlandırılıyor

### OmenFlow

OmenFlow şu komutları kullanıyor:
- `SetAuto`
- `SetMaxFan`
- `ApplyCurve`
- `SetThermalProfile`
- `SetGpuMode`
- `SetGpuPower`
- `SetLighting`
- `SetLightingEffect`
- `SetBatteryCare`
- `SetUsbCharging`
- `SetThermalSafety`

Komutların çoğu çalışıyor ve Worker tarafına ulaşıyor. Ancak arayüz tarafında komut başarı durumu ilk haliyle yeterince görünür değildi; bu yüzden UI komut hatalarını geri alacak şekilde sertleştirildi.

## 4. Komut Bazlı Karşılaştırma

### 4.1 Fan Auto / Max / Custom

| Alan | OmenCore | OmenFlow |
| --- | --- | --- |
| Auto | Fan durumunu state machine ile tekrar BIOS kontrolüne bırakır | `SetAuto` komutu ile worker’a geçer |
| Max Fan | Persist/keepalive ve thermal recovery ile korunur | `SetMaxFan` komutu ile aktif edilir |
| Custom Curve | Sürekli kontrol döngüsü ve doğrulama ile yaşatılır | `ApplyCurve` ile Worker’a gönderilir |
| Geçiş Koruması | 0 RPM / revert / wake-kick / debounce var | Temel akış var, koruma daha sade |

### 4.2 Performance Mode

| Alan | OmenCore | OmenFlow |
| --- | --- | --- |
| Thermal profile | Model-capability aware, power limit ve fan policy ayrıştırılmış | `SetThermalProfile` ile WMI profil set ediliyor |
| Fan bağlama | Varsayılan olarak kapalı, opsiyonel bağlı mod | OmenFlow’da fan ve profil aynı panelde ama daha doğrudan |
| Güç limitleri | EC güç limitleri, doğrulama ve fallback’ler var | Worker tarafında `PerformanceModeService` ile uygulanıyor |

### 4.3 GPU MUX / GPU Güç

| Alan | OmenCore | OmenFlow |
| --- | --- | --- |
| MUX | Model/capability aware, daha fazla doğrulama ve fallback içerir | `SetGpuMode` ile worker üzerinden uygulanır |
| GPU güç limiti | Detaylı model bazlı TGP kontrolü var | `SetGpuPower` ile temel seviye kontrol var |

## 5. WMI / EC Komut Prensipleri

OmenFlow tarafında öne çıkan komut mantığı:
- Fan RPM: `0x38`, `0xD0-0xD3`, `0x2D`, `0x37`
- CPU sıcaklığı: `0x23`
- Max fan: `0x27`
- Manual duty / fan level: `0x2E`
- Thermal profile: `0x1A`
- GPU mode: `0x52`
- GPU power: `0x21` / `0x22`
- Keyboard: `0x20009` ailesi

OmenCore tarafında ise aynı alanlar daha çok şu prensiplerle yönetiliyor:
- Capability gating
- Backend bazlı seçici yazım
- Yüksek düzey state machine
- Geçici hatalarda UI'yi yanlış bilgiyle beslememe
- Resume / suspend güvenliği

## 6. OmenFlow İçin Pratik Farklar

OmenFlow’un güçlü tarafları:
- UI daha hafif ve daha sade
- Worker ayrımı güvenli
- Fan, performans ve ışık için temel komutlar mevcut
- TestConsole tabanı sağlam

OmenCore’a göre eksik kalan taraflar:
- RPM telemetry state machine daha zengin değil
- "unavailable" telemetry durumu yok
- Geçiş anı korumaları daha sınırlı
- Model capability gating daha kaba
- UI başarı/başarısızlık ayrımı sonradan sertleştirildi

## 7. Kısa Sonuç

Eğer hedefin OMEN Gaming Hub benzeri bir kontrol merkezi ise OmenFlow zaten doğru yöne gidiyor. Ancak OmenCore referansı şunu gösteriyor:
- Telemetriyi sadece veri değil, durum olarak ele almak gerekiyor
- WMI/EC komutları model kabiliyetlerine göre filtrelenmeli
- Fan geçişleri sırasında kısa süreli kararsızlıklar kullanıcıya açıkça gizlenmeli
- Komut başarısızlığı UI tarafında sessizce yutulmamalı

OmenFlow şu anda daha minimal ve daha temiz bir kontrol köşesi sunuyor; OmenCore ise aynı alanlarda daha fazla kararlılık ve koruma katmanı taşıyor.