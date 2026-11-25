# Prime Watcher

Prime Watcher monitors the battery levels of wireless headsets, mice, keyboards, and gamepads on Windows. Battery indicators sit next to the system tray on the primary taskbar. Click the indicators or the tray icon to open the device list. Right-click the tray icon for settings, updates, and exit. Pin a device to prioritize it on the taskbar and show its charge in the tray icon.

## Taskbar and floating widget

The taskbar indicators use the available space to the left of the tray. They hide when there is no room, when the taskbar is hidden, or when a full-screen app is active. In **Settings**, choose individual devices or entire device types, hide devices without a reading, show one or multiple devices per type, or display icons without values.

The **Widget** tab offers two modes. Keep the widget above other windows, or show it only when the pinned device reaches the low-battery threshold. The frame pulses and a warning appears for eight seconds. The widget hides afterward in alert-only mode. A new alert can appear once the device charges above the threshold and drops to it again. Drag the header to move the widget and enable click-through to send mouse input to the window underneath. The widget works with windowed and borderless games. Visibility in exclusive full-screen mode depends on the game and the Windows graphics mode.

If a wireless mouse or keyboard stops responding while asleep, Prime Watcher shows its last reading from the current session in a muted color. The tooltip includes the time of that reading. A fresh reading replaces it whether the charge rises or falls. A dash indicates that a battery reading is unavailable. Controllers that report a battery level display bars.

## Installation and updates

Download `PrimeWatcher-Setup.exe` from the [latest GitHub release](https://github.com/Lisichkinoff/Prime-Watcher/releases/latest). The x64 installer supports Windows 11 and Windows 10 LTSC 2021. It includes .NET and installs for the current user. Launching at sign-in can be disabled in settings. On first launch, Prime Watcher shows how to move its icon from the tray overflow menu to the area next to the clock.

After installation, Prime Watcher checks for stable releases at startup and once a day. It downloads updates in the background and retries after an hour if a check or download fails. **Settings → Updates** shows the installed version and download progress. Downloaded updates apply the next time Prime Watcher starts. Select **Restart now** to apply one immediately. Device selections and preferences are stored in `%LOCALAPPDATA%\PrimeWatcherSettings`.

## Device support

| Connection | Battery information |
| --- | --- |
| Bluetooth LE | Percentage from the Battery Service. When a device reports multiple batteries, Prime Watcher displays their separate readings and labels. |
| Bluetooth Classic and HID | Percentage when Windows or the device driver provides it. Detected devices remain listed when charge is unavailable. |
| USB receivers | HyperX Cloud III S Wireless, VGN 2.4G Mouse (`3554:F503`), compatible Logitech HID++ 2.0 devices, and keyboards with a `36B0:3002` receiver whose firmware reports a battery percentage through VIA. Other receivers depend on the driver. |
| XInput | Four battery levels for compatible wireless controllers. Prime Watcher shows a percentage when it is available separately through Windows or Bluetooth. |

Protocol references include [Bluetooth Battery Service](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/BAS_v1.1/out/en/index-en.html), [Logitech HID++](https://github.com/Logitech/cpg-docs/tree/master/hidpp20), [HyperX USB reports](https://github.com/Sapd/HeadsetControl/issues/486), [VGN USB reports](https://github.com/wushuai0802/myfinder-device-battery/issues/592), [VIA battery reports on the 36B0:3002 receiver](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md#urx-core75-24-ghz), and [XInput battery information](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_battery_information).

## Build and release

Run these commands on Windows with the .NET 10 SDK and PowerShell 7.

```powershell
dotnet build src/PrimeWatcher/PrimeWatcher.csproj -c Release
./scripts/package.ps1 -Version 1.0.5
```

The installer, update package, and release index go into `artifacts/release-1.0.5/`. Pushing a version tag such as `v1.0.5` builds and publishes a stable GitHub release through GitHub Actions.

Run diagnostics to inspect detected devices and reported battery levels without opening the tray panel.

```powershell
Start-Process .\artifacts\publish-1.0.5\PrimeWatcher.exe -ArgumentList '--diagnostics', 'devices.json' -Wait
```
