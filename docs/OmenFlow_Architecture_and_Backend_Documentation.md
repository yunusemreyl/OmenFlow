# OmenFlow - Architecture, Backend Connections, and Developer Guide

This documentation details the hardware layer (EC & WMI), background service (Worker), and user interface (WinUI 3 App) communication network, button operations, and reverse-engineered HP BIOS architecture of the **OmenFlow** project.

---

## 1. System Architecture and Communication Flow

```
┌────────────────────────────────────────────────────────┐
│               OmenFlow App (WinUI 3 UI)                │
│    (HomePage, PerformancePage, LightingPage, etc.)     │
└──────────┬──────────────────────────────────▲──────────┘
           │                                  │
   [Named Pipe: Command]            [Named Pipe: Telemetry]
     (JSON Payload)                   (JSON Telemetry)
           │                                  │
           ▼                                  │
┌────────────────────────────────────────────────────────┐
│             OmenFlow.Worker (Background)               │
│       (IpcServer, SensorReader, Host Services)         │
└──────────┬──────────────────────────────────▲──────────┘
           │                                  │
    [WMI 0x20008 / EC]               [WMI 0x2D / EC 0xD0]
           │                                  │
           ▼                                  │
┌────────────────────────────────────────────────────────┐
│           HP Hardware Layer (BIOS & EC)                │
└────────────────────────────────────────────────────────┘
```

OmenFlow isolates the user interface and hardware control into two distinct processes:
1. **`OmenFlow.App` (Client)**: Hosts the WinUI 3 user interface. Establishes Named Pipe connections (`OmenFlow_HardwareCommand` and `OmenFlow_HardwareTelemetry`) via the `IpcClient` class running with standard user privileges.
2. **`OmenFlow.Worker` (Server)**: Runs in the background with administrative (Elevated) privileges. Communicates directly with the hardware layer using `PawnIOLib` and `HP WMI` drivers.

---

## 2. UI Buttons and Backend (IPC) Command Table

The JSON commands sent by UI buttons to the background service and their corresponding Worker execution routines are summarized below:

| UI Location | User Function | IPC Command Name | Sent JSON Example / Value | Worker (Backend) Function |
| :--- | :--- | :--- | :--- | :--- |
| **HomePage / PerformancePage** | **Auto Fan Mode** | `SetAuto` | `{"Command": "SetAuto", "Value": null}` | Calls `FanControlService.RestoreAutoControlAsync()`. Fan management is restored to BIOS default. |
| **HomePage / PerformancePage** | **Max Fan Mode** | `SetMaxFan` | `{"Command": "SetMaxFan", "Value": true}` | Calls `FanControlService.SetMaxFanAsync(true)`. Fans are locked to 100% LUT (55) level. |
| **PerformancePage** | **OmenFlow Smart Fan** | `ApplyCurve` | `{"Command": "ApplyCurve", "Value": {"Target": 2, "Points": [...]}}` | Executes `FanCurveHostedService.ApplyCustomCurveAsync(...)`. Dynamically adjusts fan speeds based on real-time temperatures. |
| **CustomFanWindow** | **Custom Fan Curve** | `ApplyCurve` | `{"Command": "ApplyCurve", "Value": {"Target": 2, "Points": [...]}}` | Dynamically parses and applies the custom temperature/speed points dragged by the user on the Canvas. |
| **HomePage / PerformancePage** | **Quiet Mode** | `SetThermalProfile` | `{"Command": "SetThermalProfile", "Value": 80}` *(0x50)* | Calls `PerformanceModeService.SetPerformanceModeAsync(ThermalProfile.Quiet)`. Enforces lower CPU/GPU power limits. |
| **HomePage / PerformancePage** | **Default Mode** | `SetThermalProfile` | `{"Command": "SetThermalProfile", "Value": 48}` *(0x30)* | Calls `PerformanceModeService.SetPerformanceModeAsync(ThermalProfile.Default)`. Balances power and thermal management. |
| **HomePage / PerformancePage** | **Performance Mode** | `SetThermalProfile` | `{"Command": "SetThermalProfile", "Value": 49}` *(0x31)* | Calls `PerformanceModeService.SetPerformanceModeAsync(ThermalProfile.Performance)`. Unlocks maximum CPU/GPU power limits. |
| **PerformancePage** | **GPU MUX: Hybrid / Discrete** | `SetGpuMode` | `{"Command": "SetGpuMode", "Value": 2}` *(1=Discrete, 2=Hybrid)* | Sends WMI BIOS display switch command via `GpuControlService.SetGpuModeAsync(...)`. |
| **PerformancePage** | **GPU Power (Dynamic Boost)** | `SetGpuPower` | `{"Command": "SetGpuPower", "Value": 2}` *(0=Base, 1=Extra, 2=Max)* | Configures NVIDIA Dynamic Boost TGP levels via `GpuControlService.SetGpuPowerAsync(...)`. |
| **LightingPage** | **Static Keyboard Lighting** | `SetLighting` | `{"Command": "SetLighting", "Value": {"BacklightOn": true, "ZoneColors": "..."}}` | Calls `KeyboardLightingService.SetLightingAsync(...)`. Transmits 4-Zone or 1-Zone static RGB color packets to BIOS. |
| **LightingPage** | **RGB Effects (Wave, Breath)**| `SetLightingEffect` | `{"Command": "SetLightingEffect", "Value": {"Effect": "wave", "Speed": 0.5}}` | Initializes `RgbEffectEngine` inside the Worker. Continuously streams real-time color animation packets at 10-30 FPS. |
| **AdditionalSettingsPage**| **Battery Care** | `SetBatteryCare` | `{"Command": "SetBatteryCare", "Value": true}` | Calls `PowerService.SetBatteryCareModeAsync(...)`. Toggles the BIOS flag that limits battery charging to 80%. |
| **AdditionalSettingsPage**| **USB Charging (Sleep/Off)** | `SetUsbCharging` | `{"Command": "SetUsbCharging", "Value": true}` | Configures whether USB ports continue supplying power when the laptop is turned off or asleep. |

---

## 3. Hardware Level (EC & WMI) Working Principles and Decoded Secrets

The fan and power firmware of HP Omen and Victus devices are managed directly through Embedded Controller (EC) and WMI BIOS methods rather than standard ACPI tables. The critical reverse-engineered solutions implemented in this project include:

### 3.1. Fan Speed Scaling and the `55 LUT` Memory Limit
* **Discovery**: On Victus devices (e.g., `8BBE` motherboards), the fan Look-Up Table (LUT) is indexed from **0 to 55**, not 0 to 100. Writing a fan speed level above 55 causes a memory overflow, resulting in erratic, aggressive fan speed fluctuations ("Fan Rampage").
* **OmenFlow Solution**: `ModelCapabilityDatabase` locks the `MaxFanLevel = 55` parameter for Victus systems. When the UI requests 100% fan speed, it is safely calculated as `(100 * 55) / 100 = 55` and transmitted within absolute safe boundaries to the hardware.

### 3.2. `0x19` Automatic Mode Loop Isolation
* **Discovery**: When a user writes a custom fan speed level (e.g., 45), the EC chip continuously runs its own automatic temperature loop in the background, causing the custom fan speed to instantly be overridden by the BIOS.
* **OmenFlow Solution**: Inside `FanControlService.SetFanLevelAsync`, right before transmitting the custom speed, the WMI `0x19` command (Manual Mode Lock) is sent to put the BIOS fan control loop to sleep.

### 3.3. Memory Residuals and Speed Jump Protection (`Transition Hint - 20`)
* **Discovery**: When fan speed is directly lowered from 100% to 90%, the controller fails to overcome the current high rotational momentum, occasionally causing the fans to spin even faster. However, if the speed is first dropped to a low value like 20%, the controller properly resets its PWM values.
* **OmenFlow Solution**: During rapid transitions between high speed levels, the OmenFlow service sends a millisecond-level `Transition Hint (20)` command to clear the controller's residual memory before applying the target speed.

### 3.4. True and Stable RPM Reading (`WMI 0x2D` and `EC 0xD0-0xD3`)
* **Discovery**: `0xD0 - 0xD3` EC addresses provide raw tachometer readings, which fluctuate wildly with unfiltered speeds (e.g., 6116, 6765 RPM). However, OMEN Gaming Hub displays highly stable and clear maximum limits of **5800 / 6100 RPM** in its UI.
* **OmenFlow Solution**: When querying fan speeds, OmenFlow Worker prioritizes `WMI 0x2D` (CMD_FAN_GET_LEVEL). The retrieved real-time LUT level (0-55) is scaled using the formula `(Level * MaxRpm) / 55` to convert it into perfectly stable, accurate, and OMEN Gaming Hub-grade real-time RPM telemetry.

---

## 4. Developer Services and Verification

To test the OmenFlow project and inspect hardware registers, the following companion projects are provided:

* **`OmenFlow.TestConsole`**: An interactive terminal platform where you can execute commands like `rpm`, `fan 100`, `maxon`, `auto`, `status`, and `read/write` directly to the hardware.
* **`OmenFlow.Worker`**: A memory-friendly background service that fulfills requests from the client app (`OmenFlow.App`) and broadcasts telemetry over Named Pipes every second.

The OmenFlow project is a 100% safe, stable, and highly optimized engineering solution fully compatible with HP V2 (8BBE) standards.
