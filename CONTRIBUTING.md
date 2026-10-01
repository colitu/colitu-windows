# Contributing to Colitu VPN for Windows

Thanks for helping. Bug reports, translation fixes and focused pull requests
are welcome.

## Before you start

- **Security problems:** do not open an issue; follow [SECURITY.md](SECURITY.md).
- **Account, payment or connection problems:** these are handled by support at
  <https://colitu.com/support>, not in this repository.
- For larger changes, open an issue first so we can agree on the approach.

## Building

```
cd v2rayN
dotnet build v2rayN.sln -c Release
dotnet test ServiceLib.Tests/ServiceLib.Tests.csproj -c Release
```

See the README for the full toolchain.

## Pull requests

- Keep each pull request to one change, and describe what it fixes and how you
  tested it.
- Follow the style of the surrounding code; do not reformat unrelated files.
- Add or update tests for behaviour changes.
- Do not commit secrets, signing keys, `local.properties`, `signing.properties`
  or personal configuration.
- The app speaks Russian, English and Turkish; a new user-facing string needs
  all three.
- CI (build, tests and CodeQL) must pass.

## Licence and trademarks

The code is licensed under GPL-3.0; by contributing you agree that your
contribution is published under the same licence. The Colitu name and logo
are not covered by the GPL: if you publish your own build, use your own name
and logo.

## Code of conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
