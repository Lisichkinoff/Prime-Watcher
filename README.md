# Prime Watcher

Prime Watcher показывает заряд наушников, мышей, клавиатур и геймпадов. Рядом с областью уведомлений на основной панели задач отображается ряд «значок устройства + заряд». Нажатие на ряд или левый клик по значку в трее открывает список. Правый клик открывает настройки, проверку обновлений и выход. Кнопка «Закрепить» устанавливает приоритет устройства и выводит его заряд на значок в трее.

Ряд автоматически использует свободное место слева от трея и скрывается, когда оно заканчивается, панель задач скрыта или на экране активно полноэкранное приложение. Ряд можно отключить в настройках.

## Установка

Скачайте `PrimeWatcher-Setup.exe` из [GitHub Releases](https://github.com/Lisichkinoff/Prime-Watcher/releases/latest). Сборка предназначена для Windows 11 и Windows 10 LTSC 2021 на x64. Установщик работает для текущего пользователя и включает .NET. Автозапуск можно отключить в настройках.

В первый раз Prime Watcher покажет, как перенести значок из меню скрытых значков к часам. При отключении устройства приложение показывает последнее измерение и время его получения в текущем сеансе.

## Поддержка устройств

| Подключение | Что показывает приложение |
| --- | --- |
| Bluetooth LE | Процент из Battery Service. Если доступны отдельные батареи, отображаются все показания и переданные устройством обозначения. |
| Bluetooth Classic и HID | Процент, если его передаёт Windows или драйвер. Для обнаруженного устройства без показаний выводится «Заряд недоступен». |
| USB-приёмники | Предусмотрено чтение HyperX Cloud III S Wireless, VGN 2.4G Mouse (`3554:F503`) и совместимых моделей на Logitech HID++ 2.0. Для совместимых Logitech отображается отдельная строка на каждое ответившее устройство. Остальные приёмники зависят от данных драйвера. |
| XInput | Один из четырёх уровней заряда беспроводного геймпада. Процент выводится, если он доступен отдельно через Windows или Bluetooth. |

Описание [Bluetooth Battery Service](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/BAS_v1.1/out/en/index-en.html), [Logitech HID++](https://github.com/Logitech/cpg-docs/tree/master/hidpp20), измерения USB-обмена [HyperX](https://github.com/Sapd/HeadsetControl/issues/486) и [VGN](https://github.com/wushuai0802/myfinder-device-battery/issues/592), документация [XInput](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_battery_information).

## Сборка и выпуск

В Windows с .NET SDK 10 и PowerShell 7:

```powershell
dotnet build src/PrimeWatcher/PrimeWatcher.csproj -c Release
./scripts/package.ps1 -Version 1.0.1
```

Установщик, пакет обновления и индекс появятся в `artifacts/release-1.0.1/`. Тег вида `v1.0.1` запускает сборку и публикацию стабильного релиза через GitHub Actions. Приложение проверяет стабильные релизы при запуске и раз в сутки, а после ошибки повторяет попытку через час. Загруженное обновление можно применить сразу или при следующем запуске. Выбранное устройство и настройки хранятся в `%LOCALAPPDATA%\PrimeWatcherSettings`.

Для проверки источников заряда без запуска панели выполните:

```powershell
Start-Process .\artifacts\publish-1.0.1\PrimeWatcher.exe -ArgumentList '--diagnostics', 'devices.json' -Wait
```

План и сценарии ручной проверки находятся в [PLAN.md](PLAN.md).
