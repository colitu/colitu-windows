# Colitu VPN для Windows

[English](README.md) · **Русский**

Десктопный клиент [Colitu VPN](https://colitu.com) для Windows с открытым
исходным кодом. Управление ядрами, системный прокси, TUN, маршрутизация и DNS
взяты из [v2rayN](https://github.com/2dust/v2rayN), а поверх работает единое
окно Colitu: аккаунт, серверы и подключение через Colitu API.

| | |
|---|---|
| Приложение | `Colitu VPN` (`ColituVPN.exe`), WPF на .NET 8 |
| Версия | `2.4.0` (`v2rayN/Directory.Build.props`) |
| ОС | Windows 10 / 11, x64 |
| Языки | русский, английский, турецкий |
| Сайт | <https://colitu.com> · [загрузки](https://colitu.com/downloads/windows) |
| Лицензия | [GPL-3.0](LICENSE) |

> Для подключения нужен аккаунт Colitu. В приложении нет зашитых серверов:
> список серверов и профили подключения Colitu API выдаёт отдельно для каждого устройства.

## Возможности

- **Автоматический выбор протокола.** Hysteria2 работает на встроенном ядре
  [sing-box](https://github.com/SagerNet/sing-box), а VLESS Reality, Trojan и
  Shadowsocks работают на [Xray](https://github.com/XTLS/Xray-core). Кандидаты
  перебираются по очереди, пока трафик действительно не пойдёт. Результат
  отправляется на сервер, чтобы сервис ранжировал протоколы для каждой сети.
- **Kill switch** на базе Windows Filtering Platform, по тому же принципу, что и
  в клиенте WireGuard. Пока он включён, в сеть могут выходить только ядра VPN,
  само приложение, loopback, TUN-адаптер, локальная сеть и DHCP. Фильтры живут
  в динамической сессии WFP, поэтому даже после сбоя приложения компьютер не
  останется без интернета.
- **Всё в одном окне:** вход и регистрация, подтверждение e-mail, локации,
  тарифы и оплата, аккаунт и устройства, поддержка с перепиской по обращениям.
- **Безопасное хранение токенов.** Токены шифруются Windows DPAPI
  (`CurrentUser`). Refresh-токены одноразовые и заменяются при каждом обновлении.
- **Проверенные автообновления.** Обновления скачиваются по HTTPS с
  `colitu.com` и устанавливаются, только если их SHA-256 совпадает с
  опубликованным `latest.json`.

## Как устанавливается соединение

1. `POST /auth/login` (или `/auth/register`) возвращает пару access/refresh токенов.
2. `POST /devices/register` создаёт устройство; его `id` передаётся в заголовке
   `X-Device-ID` в следующих запросах.
3. `GET /me`, `/me/entitlement`, `/devices`, `/me/usage` и `/servers` заполняют интерфейс.
4. При выборе локации вызывается `PUT /me/preferences`, затем
   `GET /config?protocol=auto` возвращает основной профиль и запасные.
5. Профили преобразуются в ссылки `vless://`, `trojan://`, `ss://` и
   `hysteria2://`, импортируются в ядро v2rayN и проверяются по очереди.
6. Результат отправляется в `POST /client/protocol-observations`.

Цены, доступность и лимиты устройств приложение само не вычисляет: источник
истины всегда сервер.

## Структура проекта

```
v2rayN/
  v2rayN/                 приложение WPF (сборка ColituVPN)
    Services/Colitu*.cs   API, авторизация, VPN, kill switch, оплата, поддержка, обновления, локализация
    Views/ColituMainWindow*   оболочка Colitu, по одному partial-файлу на страницу
    Styles/               тема и ресурсы бренда Colitu
  ServiceLib/             ядро v2rayN, генерация конфигов, маршрутизация, DNS
  *.Tests/                юнит-тесты (xUnit)
xray-dosyalari/           файлы Xray для установщика (xray.exe не хранится в git)
singbox-dosyalari/        файлы sing-box для Hysteria2 (sing-box.exe не хранится в git)
installer/ColituVPN.iss   скрипт Inno Setup
scripts/build-installer.ps1
design/                   макеты интерфейса
```

## Сборка

Нужны Windows, .NET 8 SDK с компонентом Windows Desktop и
[Inno Setup 6](https://jrsoftware.org/isinfo.php) для установщика.

```powershell
git clone https://github.com/cyberlexs/colitu-windows.git
cd colitu-windows\v2rayN
dotnet build v2rayN.sln -c Release
dotnet test v2rayN.sln -c Release
```

### Установщик

Исполняемые файлы ядер не хранятся в git. Перед сборкой установщика скачайте
их из официальных релизов и сверьте хеши:

- `xray.exe` из [релизов Xray-core](https://github.com/XTLS/Xray-core/releases)
  (`Xray-windows-64.zip`) в папку `xray-dosyalari/`; нужная версия и SHA-256
  указаны в [`xray-dosyalari/README.md`](xray-dosyalari/README.md).
- `sing-box.exe` из [sing-box v1.14.2](https://github.com/SagerNet/sing-box/releases/tag/v1.14.2)
  (`windows-amd64`) в папку `singbox-dosyalari/`.

Затем выполните:

```powershell
.\scripts\build-installer.ps1 -Version 2.4.0
```

В `artifacts/installer/` появятся `ColituVPN-Setup-<версия>-x64.exe`, его копия
с постоянным именем `ColituVPN-Setup-x64.exe` и `latest.json` для обновлений.
Подпись установщика сертификатом для подписи кода убирает предупреждение
SmartScreen.

### Другой адрес API

Задайте переменную окружения `COLITU_API_BASE_URL` или создайте файл
`guiConfigs/colitu-api.json` рядом с приложением:

```json
{ "apiBaseUrl": "https://staging.example.com/api/v1" }
```

## Безопасность

Если вы нашли уязвимость, пожалуйста, не создавайте публичный issue. Напишите
на **support@colitu.com**, и мы с вами свяжемся.

## Благодарности

Colitu для Windows основан на этих проектах:

- [v2rayN](https://github.com/2dust/v2rayN) (GPL-3.0): форк, на котором построен клиент
- [Xray-core](https://github.com/XTLS/Xray-core) (MPL-2.0)
- [sing-box](https://github.com/SagerNet/sing-box) (GPL-3.0)
- [Wintun](https://www.wintun.net/)

## Лицензия

Проект является форком v2rayN и распространяется по лицензии
[GNU General Public License v3.0](LICENSE). Встроенные сторонние компоненты
сохраняют свои лицензии (см. `xray-dosyalari/` и `singbox-dosyalari/`).

Название и логотип «Colitu» являются товарными знаками Colitu и не подпадают
под действие GPL. Если вы публикуете форк, используйте собственное название и
оформление.
