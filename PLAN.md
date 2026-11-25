# Prime Watcher for Windows implementation plan

**Prime Watcher** starts at Windows sign-in and runs in the system tray. Clicking its icon next to the clock opens a compact panel with battery readings for headsets, mice, keyboards, and gamepads. Users install it with `PrimeWatcher-Setup.exe`. The app fetches updates from GitHub Releases.

## 1. Check what battery data devices report

Start with a prototype and four physical devices. Record the model, connection method, and available readings for each one. This identifies supported models before work begins on the interface.

| Device | How to read the battery |
| --- | --- |
| Headset | Check Windows device properties and the standard Battery Service for Bluetooth LE. Show separate readings for the left earbud, right earbud, and charging case when the model reports them. |
| Mouse and keyboard | For Bluetooth, check Windows device properties and the Battery Service. For 2.4 GHz USB receivers, check HID reports or the manufacturer's interface. Show one row per device when a receiver serves multiple peripherals. |
| Gamepad | Show a percentage when available. Use XInput for compatible controllers that report one of four battery levels: empty, low, medium, or full. |

Display a percentage when the device or driver reports one. Display a label such as “Low battery” when only a battery level is available. Keep a connected device in the list and label its battery status as unavailable when it supplies no reading. [Bluetooth GATT on Windows](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client), [XInput battery levels](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_battery_information)

## 2. Collect battery readings in the background

- Discover connected devices at startup. Refresh the list when devices connect or disconnect and when the computer resumes from sleep.
- Subscribe to battery change notifications when the device provides them. Poll the remaining devices while they are connected. [Bluetooth GATT notifications](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)
- On disconnect, keep the last reading and its timestamp. Request a fresh reading on reconnection.

## 3. Use the system tray as the main interface

- The icon next to the clock shows the selected device's charge. Let the user pin a device to see its charge at a glance. Update the icon when the device disconnects.
- Left-click opens the device list with the name, battery reading, and connection status. Right-click opens a menu with settings, update checks, and exit.
- Closing the panel leaves monitoring active. Provide an option to launch at sign-in and restore the tray icon after File Explorer restarts.
- On first launch, show how to move the icon out of the notification area overflow menu. Windows lets the user choose whether it remains visible next to the clock. [Windows notification area](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)

## 4. Package the Windows installer

Build the app in C# and .NET with WPF for the panel and Velopack for installation and updates. Produce `PrimeWatcher-Setup.exe` to install for the current user and start the app in the tray. Store settings and the pinned device outside the application directory so they survive updates. [Velopack installers](https://docs.velopack.io/packaging/installer)

## 5. Fetch updates from GitHub Releases

1. Use GitHub Actions to build and publish a stable release with the installer, update package, and Velopack index.
2. Check for updates at startup and once a day using `GithubSource`. Offer a manual check in the tray menu.
3. Download new releases in the background and verify the update package. Offer to restart immediately. Otherwise, apply the package at the next launch.
4. Keep battery monitoring active if GitHub is unavailable or a download is interrupted. Retry later. [Velopack update sources](https://docs.velopack.io/integrating/update-sources)

## 6. Verify the release

- **Devices and tray.** Install on a clean Windows system and connect one device of each type. Verify readings, disconnect and reconnect a device, then check for current readings and duplicate entries after sleep and sign-in.
- **Updates.** Install version `1.0.0`, pin a device, and publish a test release of `1.0.1`. Confirm that the app discovers it, recovers from an interrupted download, and returns to the tray with the new version and the same settings.
