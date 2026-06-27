# OmenFlow

**OmenFlow** is a lightweight, modern, and reliable hardware management and lighting control application designed for HP Omen and Victus laptops (particularly 8BBE motherboard series and similar models), operating directly at the hardware level via Embedded Controller (EC) and WMI methods.

> [!WARNING]
> **Project Status: Early Skeleton Stage (Work in Progress)**
> This project is currently in a **very early skeleton stage**. While the architectural foundation, IPC communication pipelines, and core hardware control services have been established, active development is ongoing. As an early-stage release, it may lack certain features or contain bugs.

---

## 📸 Screenshots

Here is a glimpse of the modern WinUI 3 interface designed for OmenFlow:

### Main Dashboard & Performance Control
![Main Page](screenshots/mainpage.png)

### Keyboard Lighting & RGB Effects
![Keyboard Lighting Page](screenshots/keyboardpage.png)

---

## 📌 Project Goals & Features

The fan, performance, and lighting components of HP Omen and Victus devices are managed via Embedded Controller (EC) and HP WMI methods rather than conventional ACPI tables. OmenFlow separates the user interface and background hardware logic into two independent layers, ensuring exceptional stability, minimal resource consumption, and uncompromised safety:

- **Decoupled Architecture (IPC)**: The user interface (`OmenFlow.App`) runs with standard user privileges on WinUI 3, dispatching hardware commands over Named Pipes to an administrative background service (`OmenFlow.Worker`).
- **Safe Fan Management**: Strictly adheres to the `55` Look-Up Table (LUT) memory limit on Victus devices, preventing memory overflows and aggressive speed fluctuations ("Fan Rampage").
- **Smart & Custom Fan Curves**: Implements the `0x19` WMI lock to prevent the BIOS from overriding custom fan speeds. Utilizes millisecond-level memory clearing routines for smooth and stable RPM transitions.
- **Accurate Telemetry**: Delivers filtered, stable, and accurate real-time RPM/LUT calculations matching OMEN Gaming Hub standards.

---

## 📂 Directory Structure

The project files and modules are organized as follows:

```
OmenFlow/
│
├── docs/                                        # Architectural design and detailed developer documentation
│   └── OmenFlow_Architecture_and_Backend_Documentation.md
│
├── screenshots/                                 # UI screenshots for documentation
│   ├── mainpage.png                             # Dashboard & performance settings screenshot
│   └── keyboardpage.png                         # RGB lighting configuration screenshot
│
├── scripts/                                     # Developer, testing, and conversion scripts
│   ├── CheckLhm.cs                              # LibreHardwareMonitor property validation script
│   ├── PrintProps.cs                            # Sensor properties export script
│   └── convert_ico.ps1                          # PNG to ICO conversion utility
│
├── OmenFlow.App/                                # WinUI 3 based user interface (Client)
├── OmenFlow.Core/                               # Shared models, IPC protocols, and utility helpers
├── OmenFlow.Hardware/                           # HP WMI and Embedded Controller (EC) access libraries
├── OmenFlow.TestConsole/                        # Interactive terminal console for real-time EC/WMI testing
├── OmenFlow.Worker/                             # Elevated background hardware communication service (Server)
│
├── OmenFlow.slnx                                # Visual Studio Solution file
└── README.md                                    # Main project documentation
```

---

## 📖 Detailed Documentation

For a deep dive into the system architecture, IPC command tables (JSON payloads), EC/WMI working principles, and decoded HP BIOS secrets, please review the [Architecture and Backend Documentation](docs/OmenFlow_Architecture_and_Backend_Documentation.md).

---

## 🤝 Acknowledgments & Credits

OmenFlow was heavily inspired by and built upon outstanding open-source research and reverse-engineering efforts. We express our deepest gratitude to the following projects for laying the foundational knowledge for this application:

- **[omencore](https://github.com/theantipopau/omencore)**: An incredible reverse-engineering project that paved the way for decoding HP Omen WMI and EC layers, serving as a fundamental resource for understanding WMI protocols.
- **[OmenMon-Reborn](https://github.com/seakyy/OmenMon-Reborn)**: An excellent project featuring vital research and practical solutions regarding EC fan control limits, memory overflows (the 55 LUT constraint), and fan control mechanisms.

---

## 📄 License

This project is developed to be shared with the open-source community. Please refer to the `LICENSE` file for more details.
