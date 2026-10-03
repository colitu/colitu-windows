sing-box rule sets for the "Russian sites direct" rules (ColituVpnService.BuildColituRoutingRules).
Copied into bin\srss\ at build time; the core layer uses a local bin\srss\<tag>.srs before it would download one
(raw.githubusercontent.com is not reachable from Russia, so they must ship with the app).
Xray reads the same rules from xray-dosyalari\geoip.dat and geosite.dat.

Source: https://github.com/2dust/sing-box-rules (branches rule-set-geosite and rule-set-geoip), 2026-10-02.

```text
8606946fe03d4e64569b8309b007faad2d16e072832761fe17c52bcb15aca3c7  geosite-category-ru.srs
c036ff04fa075e901bc9a28ce4d0746faf45353b7ce16b7b42003e6fc14eb6e7  geoip-ru.srs
```
