# Colitu Windows çekirdek dosyaları

`scripts/build-installer.ps1` bu klasördeki dosyaları kurulum paketine koyar.

| Dosya | Kaynak | Git'te |
| --- | --- | --- |
| `xray.exe` | [XTLS/Xray-core](https://github.com/XTLS/Xray-core/releases) `Xray-windows-64.zip` — şu an **Xray 26.5.3 (228f1e1)** | Hayır (`*.exe` yok sayılır) |
| `wintun.dll` | [wintun.net](https://www.wintun.net/) amd64 | Evet |
| `geoip.dat`, `geosite.dat` | Xray yönlendirme verileri | Evet |

Kurulum paketi üretmeden önce `xray.exe` dosyasını resmi Xray-core sürümünden
indirip bu klasöre koyun ve SHA256 değerini yayın notuyla karşılaştırın.
Kullanılan ikilinin SHA256 değeri (26.5.3):

```text
47f612eff0a553c982a3e2806e54f94bee0639d0f3f0fa11116a11bbd6abda3f  xray.exe
```

Xray-core MPL-2.0 (`LICENSE`), Wintun kendi lisansı (`LICENSE-wintun.txt`) ile dağıtılır.
