# Colitu VPN для Windows

[![Build](https://img.shields.io/github/actions/workflow/status/colitu/colitu-windows/build.yml?branch=main&style=flat-square&label=build&labelColor=101014)](https://github.com/colitu/colitu-windows/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/colitu/colitu-windows?style=flat-square&labelColor=101014&color=7c6cff)](https://github.com/colitu/colitu-windows/releases/latest)
[![License](https://img.shields.io/badge/license-GPL--3.0-7c6cff?style=flat-square&labelColor=101014)](LICENSE)
[![Colitu Network](https://img.shields.io/endpoint?url=https%3A%2F%2Fstatus.colitu.com%2Fapi%2Fgithub-badge%3Fcomponent%3Dnetwork&style=flat-square)](https://status.colitu.com)

[English](README.md) · **Русский**

Десктопный клиент [Colitu VPN](https://colitu.com) для Windows с открытым
исходным кодом. Единое окно приложения отвечает за аккаунт, локации и
подключение через Colitu API; под ним работает слой ядра, который запускает
Xray и sing-box и управляет системным прокси, TUN-адаптером, маршрутизацией и DNS.

| | |
|---|---|
| Приложение | `Colitu VPN` (`ColituVPN.exe`), WPF на .NET 8 |
| Версия | `2.5.2` (`src/Directory.Build.props`) |
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
- **Проверенные автообновления.** `latest.json` подписывается офлайн
  (ECDSA P-256), и приложение принимает только манифест, который проходит
  проверку встроенным открытым ключом. Установщик скачивается по HTTPS с
  `colitu.com` в папку, доступную для записи только администраторам, и
  запускается, только если его SHA-256 совпадает с подписанным манифестом.
- **Приватные журналы.** Ядра VPN пишут журнал на уровне warning; строки о
  DNS-запросах и соединениях отбрасываются, остальные имена хостов
  скрываются, поэтому посещённые сайты не попадают ни на диск, ни в обращения
  в поддержку. Папки сессии, конфигурации и журналов доступны только
  администраторам.

## Как устанавливается соединение

1. `POST /auth/login` (или `/auth/register`) возвращает пару access/refresh токенов.
2. `POST /devices/register` создаёт устройство; его `id` передаётся в заголовке
   `X-Device-ID` в следующих запросах.
3. `GET /me`, `/me/entitlement`, `/devices`, `/me/usage` и `/servers` заполняют интерфейс.
4. При выборе локации вызывается `PUT /me/preferences`, затем
   `GET /config?protocol=auto` возвращает основной профиль и запасные.
5. Профили преобразуются в ссылки `vless://`, `trojan://`, `ss://` и
   `hysteria2://`, импортируются в слой ядра (`src/ServiceLib`) и проверяются по очереди.
6. Результат отправляется в `POST /client/protocol-observations`.

Цены, доступность и лимиты устройств приложение само не вычисляет: источник
истины всегда сервер.

## Структура проекта

```
src/
  ColituVPN/              приложение WPF (ColituVPN.exe)
    Services/Colitu*.cs   API, авторизация, VPN, kill switch, оплата, поддержка, обновления, локализация
    Views/ColituMainWindow*   оболочка Colitu, по одному partial-файлу на страницу
    Styles/               тема и ресурсы бренда Colitu
  ServiceLib/             слой ядра: генерация конфигов, маршрутизация, DNS, процессы ядер
  AmazTool/               помощник, заменяющий файлы при обновлении
  *.Tests/                юнит-тесты (xUnit)
  ColituVPN.sln
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
cd colitu-windows\src
dotnet build ColituVPN.sln -c Release
dotnet test ColituVPN.sln -c Release
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
pwsh .\scripts\build-installer.ps1 -Version 2.5.2
```

В `artifacts/installer/` появятся `ColituVPN-Setup-<версия>-x64.exe`, его копия
с постоянным именем `ColituVPN-Setup-x64.exe` и `latest.json` для обновлений.
Подпись установщика сертификатом для подписи кода убирает предупреждение
SmartScreen.

### Другой адрес API

Создайте файл `guiConfigs/colitu-api.json` рядом с приложением (адрес
`https://`; отладочные сборки также читают переменную `COLITU_API_BASE_URL`):

```json
{ "apiBaseUrl": "https://staging.example.com/api/v1" }
```

## Безопасность

Если вы нашли уязвимость, пожалуйста, не создавайте публичный issue. Напишите
на **support@colitu.com**, и мы с вами свяжемся.

## Лицензия

Colitu VPN для Windows распространяется по лицензии
[GNU General Public License v3.0](LICENSE). В состав входят компоненты с
открытым исходным кодом (Xray-core, sing-box, Wintun и другие), которые
сохраняют свои лицензии; полный список — в файле [NOTICE](NOTICE).

Название и логотип «Colitu» являются товарными знаками Colitu и не подпадают
под действие GPL. Если вы распространяете изменённую версию, используйте
собственное название и оформление.
