# Security policy

## Supported versions

Only the latest release of Colitu VPN for Windows receives security fixes. The app
updates itself; the current version is listed on
<https://colitu.com/download> and under [Releases](../../releases).

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Send the details to **support@colitu.com** with `Security` in the subject, or
use the form at <https://colitu.com/support>. Please include:

- the affected version and Windows version,
- steps to reproduce or a proof of concept,
- the impact you expect (what an attacker could do).

What to expect:

- an acknowledgement within 3 working days,
- an assessment and a planned fix date within 10 working days,
- credit in the release notes if you want it.

Please keep the details private until a fixed version is released. We will
not take legal action against research done in good faith that respects user
privacy, does not degrade the service and stays within the scope below.

## Scope

In scope: this repository's code and the release files built from it
(`ColituVPN-Setup-<version>-x64.exe`), the update mechanism and the app's communication with
the Colitu API.

Out of scope: denial of service, social engineering, physical attacks and
problems in upstream projects (report those upstream, e.g. Xray-core,
sing-box, v2rayN/v2rayNG).

The machine-readable contact is at
<https://colitu.com/.well-known/security.txt>, and the technical security
overview is at <https://colitu.com/security>.

## Verifying a release

Every release is tagged (`vX.Y.Z`) and the release page lists the files and
their SHA-256 checksums (`SHA256SUMS`). The same checksums are on
<https://colitu.com/open-source#verify>, next to the commit each tag points to.

```
Get-FileHash .\ColituVPN-Setup-<version>-x64.exe -Algorithm SHA256
```

The update manifest (`latest.json`) is signed with ECDSA P-256. The app verifies the signature with the public key built into it, then compares the installer's SHA-256 with the manifest before running it.

---

## Сообщить об уязвимости

Пожалуйста, не открывайте публичный issue. Напишите на **support@colitu.com**
с темой `Security`, приложив версию, шаги воспроизведения и ожидаемое
влияние. Мы ответим в течение 3 рабочих дней и просим не раскрывать детали
до выхода исправленной версии.
