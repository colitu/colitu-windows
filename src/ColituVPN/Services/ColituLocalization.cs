using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace v2rayN.Services;

/// <summary>
/// App strings in Russian, Turkish and English, matching the website's wording
/// (web/portal/lib/i18n). XAML binds through <see cref="T"/>; switching the
/// language refreshes every bound text without reopening windows.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    public static readonly string[] Languages = ["ru", "tr", "en"];

    private Loc()
    {
        Language = DefaultLanguage();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public string Language { get; private set; }

    public CultureInfo Culture => Language switch
    {
        "ru" => CultureInfo.GetCultureInfo("ru-RU"),
        "tr" => CultureInfo.GetCultureInfo("tr-TR"),
        _ => CultureInfo.GetCultureInfo("en-US")
    };

    public string this[string key] => Get(key);

    public void SetLanguage(string? language)
    {
        var next = Normalize(language);
        if (next == Language) return;
        Language = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        Changed?.Invoke();
    }

    public static string Normalize(string? language)
    {
        var value = (language ?? "").Trim().ToLowerInvariant();
        return Languages.Contains(value) ? value : DefaultLanguage();
    }

    /// <summary>Russian unless Windows itself runs in Turkish or English, like the website.</summary>
    public static string DefaultLanguage()
    {
        var system = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return system switch
        {
            "tr" => "tr",
            "en" => "en",
            _ => "ru"
        };
    }

    public string Get(string key)
    {
        if (Strings.TryGetValue(key, out var values))
        {
            var index = Array.IndexOf(Languages, Language);
            return values[index < 0 ? 0 : index];
        }
        return key;
    }

    public string Format(string key, params (string Name, object Value)[] args)
    {
        var text = Get(key);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{" + name + "}", Convert.ToString(value, Culture));
        }
        return text;
    }

    /// <summary>Counted noun with the right plural form ("3 устройства", "5 дней").</summary>
    public string Count(string noun, long n)
    {
        var form = Language switch
        {
            "ru" => RussianForm(n),
            "en" => n == 1 ? "one" : "many",
            _ => "one"
        };
        return Format($"{noun}.{form}", ("n", n.ToString("N0", Culture)));
    }

    private static string RussianForm(long n)
    {
        var mod10 = Math.Abs(n) % 10;
        var mod100 = Math.Abs(n) % 100;
        if (mod10 == 1 && mod100 != 11) return "one";
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return "few";
        return "many";
    }

    public static bool Has(string key) => Strings.ContainsKey(key);

    public static IReadOnlyCollection<string> Keys => Strings.Keys;

    public static string[] ValuesOf(string key) => Strings[key];

    // key → [ru, tr, en]
    private static readonly Dictionary<string, string[]> Strings = new()
    {
        // Shell
        ["nav.home"] = ["Главная", "Ana sayfa", "Home"],
        ["nav.locations"] = ["Локации", "Konumlar", "Locations"],
        ["nav.plan"] = ["Тариф", "Paket", "Plan"],
        ["nav.account"] = ["Аккаунт", "Hesap", "Account"],
        ["nav.settings"] = ["Настройки", "Ayarlar", "Settings"],
        ["window.minimize"] = ["Свернуть", "Küçült", "Minimize"],
        ["window.close"] = ["Свернуть в трей", "Tepsiye küçült", "Hide to tray"],
        ["loading.session"] = ["Восстанавливаем сеанс…", "Oturumunuz açılıyor…", "Restoring your session…"],
        ["update.available"] = ["Обновление {version}", "Güncelleme {version}", "Update {version}"],
        ["update.downloading"] = ["Загрузка {percent}%", "İndiriliyor %{percent}", "Downloading {percent}%"],
        ["update.title"] = ["Доступна версия Colitu {version}", "Colitu {version} hazır", "Colitu {version} is available"],
        ["update.ask"] = ["Обновить сейчас? В старой версии подключение и вход в аккаунт могут работать с ошибками.", "Şimdi güncellemek ister misiniz? Eski sürümde bağlantı ve hesap işlemlerinde sorunlar yaşanabilir.", "Update now? The old version may have problems connecting or signing in."],
        ["update.force"] = ["Эта версия больше не поддерживается. Обновите приложение, чтобы продолжить.", "Bu sürüm artık desteklenmiyor. Devam etmek için uygulamayı güncelleyin.", "This version is no longer supported. Update the app to continue."],
        ["update.now"] = ["Обновить сейчас", "Şimdi güncelle", "Update now"],
        ["update.later"] = ["Позже", "Daha sonra", "Later"],
        ["update.restart"] = ["Colitu закроется, установит обновление и откроется снова.", "Colitu kapanıp güncellemeyi kuracak ve yeniden açılacak.", "Colitu will close, install the update and open again."],
        ["update.installing"] = ["Устанавливаем обновление…", "Güncelleme kuruluyor…", "Installing update…"],
        ["update.failed"] = ["Не удалось установить обновление. Попробуйте позже.", "Güncelleme kurulamadı. Daha sonra tekrar deneyin.", "The update could not be installed. Please try again later."],

        // Connection status
        ["status.protected"] = ["Защищено", "Korunuyor", "Protected"],
        ["status.unprotected"] = ["Не защищено", "Korunmuyor", "Not protected"],
        ["status.connecting"] = ["Подключение…", "Bağlanıyor…", "Connecting…"],
        ["status.reconnecting"] = ["Переподключение…", "Yeniden bağlanıyor…", "Reconnecting…"],
        ["status.disconnecting"] = ["Отключение…", "Bağlantı kesiliyor…", "Disconnecting…"],
        ["status.blocked"] = ["Интернет заблокирован", "İnternet engelli", "Internet blocked"],

        // Home
        ["home.kicker"] = ["COLITU VPN · WINDOWS", "COLITU VPN · WINDOWS", "COLITU VPN · WINDOWS"],
        ["home.title.off"] = ["Соединение не защищено", "Bağlantınız korunmuyor", "Your connection isn’t protected"],
        ["home.title.on"] = ["Вы под защитой", "Korunuyorsunuz", "You’re protected"],
        ["home.title.connecting"] = ["Защищаем соединение", "Bağlantınız güvenceye alınıyor", "Securing your connection"],
        ["home.title.noplan"] = ["Выберите тариф, чтобы начать", "Başlamak için bir paket seçin", "Choose a plan to get started"],
        ["home.sub.off"] = ["Нажмите кнопку — трафик будет зашифрован, а IP-адрес скрыт.", "Düğmeye basın; trafiğiniz şifrelenir, IP adresiniz gizlenir.", "Press the button to encrypt your traffic and hide your IP address."],
        ["home.sub.on"] = ["Трафик зашифрован и идёт через {server}.", "Trafiğiniz şifreli ve {server} üzerinden geçiyor.", "Your traffic is encrypted and routed through {server}."],
        ["home.sub.connecting"] = ["Готовим зашифрованный туннель…", "Şifreli tünel hazırlanıyor…", "Setting up the encrypted tunnel…"],
        ["home.sub.noplan"] = ["Все серверы, безлимитный трафик и защита от утечек — в одном тарифе.", "Tüm sunucular, sınırsız trafik ve sızıntı koruması tek pakette.", "Every server, unlimited traffic and leak protection in one plan."],
        ["home.title.blocked"] = ["Интернет заблокирован", "İnternet engellendi", "Internet blocked"],
        ["home.sub.blocked"] = ["VPN прервался, и kill switch закрыл интернет, чтобы ничего не утекло. Переподключаемся автоматически — или отключите защиту.", "VPN koptu; kill switch hiçbir şey sızmasın diye interneti kapattı. Otomatik olarak yeniden bağlanıyoruz; isterseniz korumayı kapatın.", "The VPN dropped, so the kill switch closed the internet to keep anything from leaking. We’re reconnecting automatically, or you can turn protection off."],
        ["home.unblock"] = ["Отключить защиту и открыть интернет", "Korumayı kapat ve interneti aç", "Turn protection off and restore internet"],
        ["home.connect"] = ["Подключить", "Bağlan", "Connect"],
        ["home.disconnect"] = ["Отключить", "Bağlantıyı kes", "Disconnect"],
        ["home.cancel"] = ["Отменить", "İptal", "Cancel"],
        ["home.tap"] = ["Нажмите, чтобы подключиться", "Bağlanmak için tıklayın", "Click to connect"],
        ["home.tapOff"] = ["Нажмите, чтобы отключить", "Kesmek için tıklayın", "Click to disconnect"],
        ["home.tapCancel"] = ["Нажмите, чтобы отменить", "İptal etmek için tıklayın", "Click to cancel"],
        ["home.protocol"] = ["Протокол", "Protokol", "Protocol"],
        // Colitu names of the transports; the technical names stay out of the UI.
        ["transport.hysteria2"] = ["Быстрый", "Hızlı", "Fast"],
        ["transport.vless-reality"] = ["Скрытный", "Gizli", "Stealth"],
        ["transport.vless-xhttp"] = ["Устойчивый", "Dayanıklı", "Resilient"],
        ["transport.trojan"] = ["Классический", "Klasik", "Classic"],
        ["transport.shadowsocks"] = ["Лёгкий", "Hafif", "Light"],
        ["transport.other"] = ["Резервный", "Yedek", "Backup"],
        ["home.changeServer"] = ["Сменить сервер", "Sunucuyu değiştir", "Change server"],
        ["home.session"] = ["Время подключения", "Bağlantı süresi", "Connection time"],
        ["home.location"] = ["ЛОКАЦИЯ", "KONUM", "LOCATION"],
        ["home.change"] = ["Изменить", "Değiştir", "Change"],
        ["home.plan"] = ["ТАРИФ", "PAKET", "PLAN"],
        ["home.quick"] = ["БЫСТРЫЕ НАСТРОЙКИ", "HIZLI AYARLAR", "QUICK SETTINGS"],
        ["home.traffic"] = ["Трафик за период", "Bu dönemki trafik", "Traffic this period"],
        ["home.trafficOf"] = ["{used} из {limit}", "{used} / {limit}", "{used} of {limit}"],
        ["home.unlimited"] = ["Безлимитный трафик", "Sınırsız trafik", "Unlimited traffic"],
        ["home.devices"] = ["Устройства", "Cihazlar", "Devices"],
        ["home.offline"] = ["Нет связи с сервером Colitu. Повторяем попытку…", "Colitu sunucusuna ulaşılamıyor. Yeniden deneniyor…", "Can’t reach Colitu right now. Retrying…"],

        ["server.auto"] = ["Лучший сервер", "En iyi sunucu", "Best server"],
        ["server.autoHint"] = ["Colitu сам выберет самый свободный и стабильный", "Colitu en boş ve kararlı sunucuyu kendisi seçer", "Colitu picks the least busy, most stable one"],
        ["server.none"] = ["Серверы появятся здесь", "Sunucular burada görünecek", "Servers will appear here"],
        ["server.load.low"] = ["Низкая нагрузка", "Düşük yük", "Low load"],
        ["server.load.medium"] = ["Средняя нагрузка", "Orta yük", "Medium load"],
        ["server.load.high"] = ["Высокая нагрузка", "Yüksek yük", "High load"],
        ["server.offline"] = ["Недоступен", "Çevrimdışı", "Offline"],
        ["server.connected"] = ["Подключено", "Bağlı", "Connected"],
        ["server.selected"] = ["Выбрано", "Seçili", "Selected"],

        // Locations
        ["locations.kicker"] = ["ЛОКАЦИИ", "KONUMLAR", "LOCATIONS"],
        ["locations.title"] = ["Выберите локацию", "Bir konum seçin", "Choose a location"],
        ["locations.sub"] = ["Серверов онлайн: {n}. При смене локации подключение переключится автоматически.", "Çevrimiçi sunucu: {n}. Konumu değiştirdiğinizde bağlantı otomatik olarak geçer.", "{n} servers online. Switching location moves your connection automatically."],
        ["locations.search"] = ["Поиск страны или города", "Ülke veya şehir ara", "Search country or city"],
        ["locations.empty"] = ["Ничего не найдено", "Sonuç bulunamadı", "Nothing found"],
        ["locations.switching"] = ["Переключаемся на {server}…", "{server} konumuna geçiliyor…", "Switching to {server}…"],
        ["locations.switched"] = ["Подключено: {server}", "Bağlandı: {server}", "Connected to {server}"],

        // Plan status
        ["plan.none"] = ["Нет активного тарифа", "Aktif paket yok", "No active plan"],
        ["plan.noneHint"] = ["Выберите тариф, чтобы подключиться.", "Bağlanmak için bir paket seçin.", "Choose a plan to connect."],
        ["plan.status.active"] = ["АКТИВЕН", "AKTİF", "ACTIVE"],
        ["plan.status.trialing"] = ["ПРОБНЫЙ", "DENEME", "TRIAL"],
        ["plan.status.expired"] = ["ИСТЁК", "SÜRESİ DOLDU", "EXPIRED"],
        ["plan.status.inactive"] = ["НЕ АКТИВЕН", "PASİF", "INACTIVE"],
        ["plan.until"] = ["До {date}", "{date} tarihine kadar", "Until {date}"],
        ["plan.left"] = ["осталось {left}", "{left} kaldı", "{left} left"],
        ["plan.choose"] = ["Выбрать тариф", "Paket seç", "Choose a plan"],
        ["plan.extend"] = ["Продлить", "Süreyi uzat", "Extend"],
        ["plan.freeName"] = ["Бесплатный тариф", "Ücretsiz plan", "Free plan"],
        ["plan.freeHint"] = ["10 ГБ каждый месяц", "Her ay 10 GB", "10 GB every month"],
        ["plan.upgrade"] = ["Перейти на безлимит", "Sınırsız trafiğe geç", "Go unlimited"],
        ["plan.status.quota"] = ["ЛИМИТ ИСЧЕРПАН", "KOTA DOLDU", "LIMIT REACHED"],
        ["plan.trialName"] = ["Пробный период", "Deneme süresi", "Free trial"],
        ["day.one"] = ["{n} день", "{n} gün", "{n} day"],
        ["day.few"] = ["{n} дня", "{n} gün", "{n} days"],
        ["day.many"] = ["{n} дней", "{n} gün", "{n} days"],
        ["hour.one"] = ["{n} час", "{n} saat", "{n} hour"],
        ["hour.few"] = ["{n} часа", "{n} saat", "{n} hours"],
        ["hour.many"] = ["{n} часов", "{n} saat", "{n} hours"],
        ["device.one"] = ["{n} устройство", "{n} cihaz", "{n} device"],
        ["device.few"] = ["{n} устройства", "{n} cihaz", "{n} devices"],
        ["device.many"] = ["{n} устройств", "{n} cihaz", "{n} devices"],
        ["location.one"] = ["{n} локация", "{n} konum", "{n} location"],
        ["location.few"] = ["{n} локации", "{n} konum", "{n} locations"],
        ["location.many"] = ["{n} локаций", "{n} konum", "{n} locations"],

        // Pricing (same wording as colitu.com/pricing)
        ["pricing.kicker"] = ["ТАРИФЫ", "PAKETLER", "PLANS"],
        ["pricing.title"] = ["Один тариф. Все серверы.", "Tek paket. Tüm sunucular.", "One plan. Every server."],
        ["pricing.sub"] = ["Тариф покупается и продлевается в личном кабинете на colitu.com.", "Paket satın alma ve süre uzatma colitu.com hesabınızdan yapılır.", "Plans are bought and renewed in your account on colitu.com."],
        ["plan.manageTitle"] = ["Подписка управляется на colitu.com", "Abonelik colitu.com üzerinden yönetilir", "Your subscription is managed on colitu.com"],
        ["plan.manageBody"] = ["Тариф, продление и устройства — в личном кабинете. Войдите с той же почтой, что и в приложении.", "Paket, süre uzatma ve cihazlar hesabınızdan yönetilir. Uygulamadaki e-posta adresinizle giriş yapın.", "Plans, renewals and devices are handled in your account. Sign in with the same e-mail as in the app."],
        ["plan.manageButton"] = ["Открыть app.colitu.com", "app.colitu.com’u aç", "Open app.colitu.com"],
        ["plan.refresh"] = ["Обновить статус", "Durumu yenile", "Refresh status"],
        ["plan.refreshHint"] = ["После изменений в кабинете вернитесь сюда и обновите статус.", "Hesabınızda değişiklik yaptıktan sonra buraya dönüp durumu yenileyin.", "After changing something in your account, come back and refresh."],
        ["plan.refreshed"] = ["Статус обновлён", "Durum güncellendi", "Status updated"],
        ["pricing.current"] = ["Текущий тариф", "Mevcut paket", "Current plan"],
        ["pricing.months"] = ["{n} МЕС.", "{n} AY", "{n} MONTHS"],
        ["pricing.oneMonth"] = ["1 МЕС.", "1 AY", "1 MONTH"],
        ["pricing.twoYears"] = ["2 ГОДА", "2 YIL", "2 YEARS"],
        ["pricing.off"] = ["Скидка {n}%", "%{n} İNDİRİM", "{n}% off"],
        ["pricing.best"] = ["САМОЕ ВЫГОДНОЕ ПРЕДЛОЖЕНИЕ", "EN AVANTAJLI · EN ÇOK TASARRUF", "BEST VALUE · BIGGEST SAVINGS"],
        ["pricing.perDevice"] = ["в месяц за устройство", "aylık, cihaz başına", "per device a month"],
        ["pricing.saving"] = ["Экономия {amount} на устройство", "Cihaz başına {amount} tasarruf", "Save {amount} per device"],
        ["pricing.devices"] = ["Количество устройств", "Cihaz sayısı", "Number of devices"],
        ["pricing.method"] = ["Способ оплаты", "Ödeme yöntemi", "Payment method"],
        ["pricing.noFee"] = ["Без комиссии", "Komisyonsuz", "No fee"],
        ["pricing.fee"] = ["Комиссия {n}%", "Komisyon %{n}", "{n}% fee"],
        ["pricing.summary"] = ["ИТОГ", "ÖZET", "SUMMARY"],
        ["pricing.regular"] = ["Обычная цена", "Normal fiyat", "Regular price"],
        ["pricing.discount"] = ["Скидка ({n}%)", "İndirim (%{n})", "Discount ({n}%)"],
        ["pricing.commission"] = ["Комиссия", "Komisyon", "Payment fee"],
        ["pricing.total"] = ["ИТОГО", "TOPLAM", "TOTAL"],
        ["pricing.pay"] = ["Перейти к безопасной оплате", "Güvenli ödemeye geç", "Continue to secure payment"],
        ["pricing.secure"] = ["Оплата проходит на защищённой странице платёжного сервиса. Доступ откроется сразу после оплаты.", "Ödeme, ödeme sağlayıcısının güvenli sayfasında yapılır. Erişiminiz ödemeden hemen sonra açılır.", "You pay on the payment provider’s secure page. Access opens as soon as the payment clears."],
        ["pricing.loadFailed"] = ["Не удалось загрузить тарифы.", "Paketler yüklenemedi.", "Plans could not be loaded."],
        ["pricing.retry"] = ["Повторить", "Tekrar dene", "Try again"],
        ["pay.checking"] = ["Проверяем оплату…", "Ödeme kontrol ediliyor...", "Checking payment…"],
        ["pay.checkingHint"] = ["Завершите оплату в браузере. Эта страница обновится автоматически.", "Ödemeyi tarayıcıda tamamlayın. Bu sayfa otomatik olarak güncellenir.", "Finish paying in your browser. This page updates by itself."],
        ["pay.reopen"] = ["Открыть страницу оплаты", "Ödeme sayfasını aç", "Open payment page"],
        ["pay.cancel"] = ["Отменить", "Vazgeç", "Cancel"],
        ["pay.success"] = ["Оплата прошла", "Ödeme alındı", "Payment received"],
        ["pay.successHint"] = ["Тариф активен. Можно подключаться.", "Paketiniz aktif. Hemen bağlanabilirsiniz.", "Your plan is active. You can connect now."],
        ["pay.failed"] = ["Оплата не прошла", "Ödeme tamamlanmadı", "Payment didn’t go through"],
        ["pay.failedHint"] = ["Деньги не списаны. Попробуйте ещё раз или выберите другой способ.", "Ücret alınmadı. Tekrar deneyin veya başka bir yöntem seçin.", "You were not charged. Try again or pick another method."],
        ["pay.back"] = ["Вернуться к тарифам", "Paketlere dön", "Back to plans"],
        ["pay.connect"] = ["Подключиться", "Bağlan", "Connect now"],

        // Account
        ["account.kicker"] = ["АККАУНТ", "HESAP", "ACCOUNT"],
        ["account.title"] = ["Ваш аккаунт", "Hesabınız", "Your account"],
        ["account.email"] = ["Электронная почта", "E-posta", "Email"],
        ["account.plan"] = ["Тариф", "Paket", "Plan"],
        ["account.validUntil"] = ["Действует до", "Geçerlilik", "Valid until"],
        ["account.devices"] = ["УСТРОЙСТВА", "CİHAZLAR", "DEVICES"],
        ["account.devicesTitle"] = ["Подключено {used} из {limit}", "{limit} cihazdan {used} tanesi bağlı", "{used} of {limit} in use"],
        ["account.thisDevice"] = ["ЭТО УСТРОЙСТВО", "BU CİHAZ", "THIS DEVICE"],
        ["account.lastSeen"] = ["Активность: {date}", "Son etkinlik: {date}", "Last active {date}"],
        ["account.remove"] = ["Отключить устройство", "Cihazı kaldır", "Remove device"],
        ["account.removeConfirm"] = ["Отключить «{name}»? На нём потребуется войти снова.", "“{name}” kaldırılsın mı? Bu cihazda yeniden giriş yapmak gerekecek.", "Remove “{name}”? It will need to sign in again."],
        ["account.manage"] = ["Управлять на colitu.com", "colitu.com’da yönet", "Manage on colitu.com"],
        ["account.signOut"] = ["Выйти", "Çıkış yap", "Sign out"],
        ["account.help"] = ["Нужна помощь?", "Yardım mı lazım?", "Need help?"],
        ["account.helpHint"] = ["Напишите нам — отвечаем быстро и по-человечески.", "Bize yazın; hızlı ve insan gibi yanıt veririz.", "Write to us. We answer quickly, and a real person reads it."],
        ["account.support"] = ["Центр поддержки", "Destek merkezi", "Support center"],
        ["account.mail"] = ["Написать на почту", "E-posta gönder", "Email support"],

        // Settings
        ["settings.kicker"] = ["НАСТРОЙКИ", "AYARLAR", "SETTINGS"],
        ["settings.title"] = ["Настройки", "Ayarlar", "Settings"],
        ["settings.language"] = ["Язык", "Dil", "Language"],
        ["settings.connection"] = ["Подключение", "Bağlantı", "Connection"],
        ["settings.mode"] = ["Режим", "Mod", "Mode"],
        ["settings.mode.proxy"] = ["Прокси", "Proxy", "Proxy"],
        ["settings.mode.tun"] = ["Весь трафик (TUN)", "Tüm trafik (TUN)", "All traffic (TUN)"],
        ["settings.mode.proxyHint"] = ["Через VPN идут только программы, использующие системный прокси. DNS-запросы, UDP/WebRTC, IPv6 и программы, игнорирующие прокси, не защищены.", "Yalnızca sistem proxy’sini kullanan uygulamalar VPN’den geçer. DNS sorguları, UDP/WebRTC, IPv6 ve proxy’yi yok sayan uygulamalar korunmaz.", "Only apps that use the system proxy go through the VPN. DNS lookups, UDP/WebRTC, IPv6 and apps that ignore the proxy are not protected."],
        ["settings.mode.tunHint"] = ["Весь трафик Windows идёт через защищённый адаптер, включая игры и мессенджеры.", "Oyunlar ve mesajlaşma dahil tüm Windows trafiği güvenli bağdaştırıcıdan geçer.", "All Windows traffic, games and messengers included, goes through the secure adapter."],
        ["settings.killSwitch"] = ["Kill switch", "Kill switch", "Kill switch"],
        ["settings.killSwitchHint"] = ["Пока VPN включён, трафик мимо VPN блокируется — и если VPN или приложение неожиданно отключатся, интернет остаётся закрытым, пока защита не вернётся. В режиме прокси программы без системного прокси остаются без интернета.", "VPN açıkken VPN dışı trafik engellenir; VPN ya da uygulama beklenmedik şekilde kapanırsa koruma dönene kadar internet kapalı kalır. Proxy modunda sistem proxy’sini kullanmayan uygulamalar internete çıkamaz.", "While the VPN is on, traffic outside it is blocked, and if the VPN or the app stops unexpectedly the internet stays closed until protection is back. In proxy mode, apps that don’t use the system proxy have no internet."],
        ["settings.dns"] = ["Защита от утечек DNS", "DNS sızıntı koruması", "DNS leak protection"],
        ["settings.dnsHint"] = ["В режиме TUN все DNS-запросы идут через VPN. В режиме прокси защищены запросы приложений, использующих системный прокси.", "TUN modunda tüm DNS sorguları VPN üzerinden gider. Proxy modunda yalnızca sistem proxy'sini kullanan uygulamaların sorguları korunur.", "In TUN mode every DNS lookup goes through the VPN. In proxy mode only apps that use the system proxy are covered."],
        ["settings.autoConnect"] = ["Автоподключение", "Otomatik bağlan", "Auto-connect"],
        ["settings.autoConnectHint"] = ["Подключаться сразу после запуска приложения.", "Uygulama açılır açılmaz bağlan.", "Connect as soon as the app starts."],
        ["settings.adBlock"] = ["Блокировка рекламы", "Reklam engelleme", "Ad blocking"],
        ["settings.adBlockHint"] = ["Реклама и трекеры блокируются на DNS-серверах Colitu. Журнал запросов не ведётся. Если какой-то сайт сломается, выключите.", "Reklam ve izleyiciler Colitu DNS sunucularında engellenir. Sorgu kaydı tutulmaz. Bir site bozulursa kapatın.", "Ads and trackers are blocked on Colitu’s DNS servers. No query log is kept. If a site breaks, turn it off."],
        ["privacy.title"] = ["Режим приватности", "Gizlilik modu", "Privacy mode"],
        ["privacy.hint"] = ["Весь трафик через VPN. Если выключено, при сервере за пределами России российские сайты и IP-адреса идут напрямую, вне VPN, и видят ваш реальный IP-адрес.", "Tüm trafik VPN'den geçsin. Kapalıyken, Rusya dışındaki bir sunucuda Rus siteleri ve IP adresleri VPN dışından doğrudan gider ve gerçek IP adresinizi görür.", "Send all traffic through the VPN. When off, Russian sites and IP addresses go directly, outside the VPN, while you use a server outside Russia, and they see your real IP address."],
        ["privacy.scope"] = ["Какие сайты и адреса?", "Hangi siteler ve adresler?", "Which sites and addresses?"],
        ["privacy.chip"] = ["Российские сайты вне VPN", "Rus siteleri VPN dışında", "Russian sites outside VPN"],
        ["privacy.notice.title"] = ["Российские сайты идут вне VPN", "Rus siteleri VPN dışından gider", "Russian sites go outside the VPN"],
        ["privacy.notice.body"] = ["Чтобы российские банки, Госуслуги и другие сервисы, которые не пускают иностранные IP-адреса, продолжали работать, при сервере за пределами России Colitu отправляет российские сайты (список доменов category-ru) и российские IP-адреса напрямую, вне VPN. Эти сайты и ваш интернет-провайдер видят для такого трафика ваш реальный IP-адрес. Весь остальной трафик идёт через VPN. Включите режим приватности, чтобы весь трафик шёл через VPN; это можно изменить в любой момент в настройках.", "Rus bankaları, Gosuslugi ve yabancı IP adreslerini kabul etmeyen diğer servisler çalışmaya devam etsin diye Colitu, Rusya dışındaki bir sunucuda Rus sitelerini (category-ru alan adı listesi) ve Rus IP adreslerini VPN dışından, doğrudan gönderir. Bu trafikte siteler ve internet sağlayıcınız gerçek IP adresinizi görür. Diğer tüm trafik VPN'den geçer. Tüm trafiğin VPN'den geçmesi için gizlilik modunu açın; bunu istediğiniz zaman ayarlardan değiştirebilirsiniz.", "To keep Russian banks, Gosuslugi and other services that refuse foreign IP addresses working, Colitu sends Russian sites (the category-ru domain list) and Russian IP addresses directly, outside the VPN, while you use a server outside Russia. For this traffic those sites and your internet provider see your real IP address. Everything else goes through the VPN. Turn on privacy mode to send all traffic through the VPN; you can change this any time in settings."],
        ["privacy.notice.keep"] = ["Оставить", "Böyle kalsın", "Keep"],
        ["privacy.notice.enable"] = ["Включить режим приватности", "Gizlilik modunu aç", "Turn on privacy mode"],
        ["privacy.details"] = ["Подробнее", "Ayrıntılar", "Details"],
        ["settings.app"] = ["Приложение", "Uygulama", "App"],
        ["settings.startup"] = ["Запускать вместе с Windows", "Windows ile başlat", "Start with Windows"],
        ["settings.startupHint"] = ["Colitu откроется в трее при входе в Windows.", "Colitu, Windows oturumu açıldığında tepside başlar.", "Colitu starts in the tray when you sign in to Windows."],
        ["settings.tray"] = ["Сворачивать в трей при закрытии", "Kapatınca tepsiye küçült", "Keep running in the tray"],
        ["settings.trayHint"] = ["Защита остаётся включённой, пока окно закрыто.", "Pencere kapalıyken koruma açık kalır.", "Protection stays on while the window is closed."],
        ["settings.about"] = ["О приложении", "Hakkında", "About"],
        ["settings.version"] = ["Версия {version}", "Sürüm {version}", "Version {version}"],
        ["settings.checkUpdates"] = ["Проверить обновления", "Güncellemeleri denetle", "Check for updates"],
        ["settings.upToDate"] = ["У вас последняя версия.", "En güncel sürümü kullanıyorsunuz.", "You’re on the latest version."],
        ["settings.updateCheckFailed"] = ["Не удалось проверить обновления. Проверьте интернет и попробуйте ещё раз.", "Güncellemeler denetlenemedi. İnternet bağlantınızı kontrol edip tekrar deneyin.", "Couldn’t check for updates. Check your internet connection and try again."],
        ["settings.checking"] = ["Проверяем…", "Denetleniyor…", "Checking…"],
        ["settings.privacy"] = ["Конфиденциальность", "Gizlilik", "Privacy"],
        ["settings.terms"] = ["Условия", "Koşullar", "Terms"],
        ["settings.website"] = ["colitu.com", "colitu.com", "colitu.com"],
        ["settings.logs"] = ["Открыть журналы", "Günlükleri aç", "Open logs"],
        ["settings.saved"] = ["Сохранено", "Kaydedildi", "Saved"],
        ["settings.reconnectHint"] = ["Изменения применятся при следующем подключении.", "Değişiklik bir sonraki bağlantıda uygulanır.", "Changes apply the next time you connect."],
        ["about.openSource"] = ["Открытый исходный код", "Açık kaynak", "Open source"],
        ["about.openSourceHint"] = ["Colitu для Windows распространяется под лицензией GPL-3.0. Код открыт на GitHub: github.com/colitu/windows", "Colitu Windows uygulaması GPL-3.0 lisanslıdır. Kaynak kodu GitHub'da: github.com/colitu/windows", "Colitu for Windows is licensed under GPL-3.0. The source code is on GitHub: github.com/colitu/windows"],
        ["about.viewSource"] = ["Открыть на GitHub", "GitHub'da aç", "View on GitHub"],
        ["about.credits"] = ["Основано на v2rayN (GPL-3.0), Xray-core (MPL-2.0) и sing-box (GPL-3.0).", "v2rayN (GPL-3.0), Xray-core (MPL-2.0) ve sing-box (GPL-3.0) üzerine kuruludur.", "Built on v2rayN (GPL-3.0), Xray-core (MPL-2.0) and sing-box (GPL-3.0)."],
        ["brand.credit"] = ["© {brand}", "© {brand}", "© {brand}"],

        // Auth
        ["auth.kicker"] = ["БЕЗОПАСНО · ПРИВАТНО · БЫСТРО", "GÜVENLİ · ÖZEL · HIZLI", "SECURE · PRIVATE · FAST"],
        ["auth.heroTitle"] = ["Свободный интернет в одно касание.", "Özgür internet, tek dokunuşla.", "The open internet, one tap away."],
        ["auth.heroSub"] = ["Серверы в разных странах, шифрование трафика и никаких журналов.", "Farklı ülkelerde sunucular, şifreli trafik ve kayıt tutmama.", "Servers in many countries, encrypted traffic and no activity logs."],
        ["auth.login"] = ["Вход", "Giriş", "Sign in"],
        ["auth.register"] = ["Регистрация", "Kayıt ol", "Sign up"],
        ["auth.loginTitle"] = ["С возвращением", "Tekrar hoş geldiniz", "Welcome back"],
        ["auth.loginSub"] = ["Войдите в аккаунт Colitu — тот же, что на colitu.com.", "Colitu hesabınızla giriş yapın; colitu.com ile aynı hesap.", "Sign in with your Colitu account, the same one you use on colitu.com."],
        ["auth.registerTitle"] = ["Создать аккаунт", "Hesap oluştur", "Create account"],
        ["reset.title"] = ["Восстановление пароля", "Şifrenizi sıfırlayın", "Reset your password"],
        ["reset.sub"] = ["Введите почту аккаунта — мы отправим на неё 6-значный код.", "Hesabınızın e-posta adresini girin; 6 haneli bir kod gönderelim.", "Enter your account email and we’ll send you a 6-digit code."],
        ["reset.codeSub"] = ["Введите код из письма и придумайте новый пароль.", "E-postadaki kodu girin ve yeni bir şifre belirleyin.", "Enter the code from the email and choose a new password."],
        ["reset.send"] = ["Отправить код", "Kod gönder", "Send code"],
        ["reset.sent"] = ["Код отправлен на {email}. Проверьте и папку «Спам».", "{email} adresine kod gönderdik. Gereksiz (spam) klasörünü de kontrol edin.", "We sent a code to {email}. Check your spam folder too."],
        ["reset.newPassword"] = ["Новый пароль", "Yeni şifre", "New password"],
        ["reset.submit"] = ["Сменить пароль и войти", "Şifreyi değiştir ve giriş yap", "Change password and sign in"],
        ["reset.changeEmail"] = ["Изменить почту", "E-postayı değiştir", "Change email"],
        ["reset.back"] = ["Вернуться ко входу", "Girişe dön", "Back to sign in"],
        ["reset.note"] = ["После смены пароля остальные устройства выйдут из аккаунта.", "Şifre değişince diğer cihazlardaki oturumlar kapanır.", "Changing the password signs you out on your other devices."],
        ["reset.done"] = ["Пароль изменён. Вы вошли в аккаунт.", "Şifreniz değiştirildi, giriş yaptınız.", "Your password was changed and you’re signed in."],
        ["auth.registerSub"] = ["Зарегистрируйтесь по почте и пользуйтесь Colitu бесплатно: 10 ГБ каждый месяц.", "E-postanızla kaydolun, Colitu’yu ücretsiz kullanın: her ay 10 GB.", "Sign up with your email and use Colitu for free: 10 GB every month."],
        ["auth.email"] = ["Электронная почта", "E-posta", "Email"],
        ["auth.emailHint"] = ["you@example.com", "ornek@eposta.com", "you@example.com"],
        ["auth.password"] = ["Пароль", "Şifre", "Password"],
        ["auth.passwordHint"] = ["Не менее 10 символов", "En az 10 karakter", "At least 10 characters"],
        ["auth.passwordRepeat"] = ["Повторите пароль", "Şifreyi tekrarla", "Repeat password"],
        ["auth.show"] = ["Показать пароль", "Şifreyi göster", "Show password"],
        ["auth.terms"] = ["Я принимаю условия и политику конфиденциальности", "Koşulları ve gizlilik politikasını kabul ediyorum", "I accept the terms and privacy policy"],
        ["auth.submitLogin"] = ["Войти", "Giriş yap", "Sign in"],
        ["auth.submitRegister"] = ["Создать аккаунт", "Hesap oluştur", "Create account"],
        ["auth.forgot"] = ["Забыли пароль?", "Şifremi unuttum", "Forgot password?"],
        ["auth.remember"] = ["Вы останетесь в аккаунте на этом компьютере.", "Bu bilgisayarda oturumunuz açık kalır.", "You’ll stay signed in on this computer."],
        ["auth.err.email"] = ["Введите корректный адрес почты.", "Geçerli bir e-posta adresi girin.", "Enter a valid email address."],
        ["auth.err.password"] = ["Пароль должен быть не короче 10 символов.", "Şifre en az 10 karakter olmalı.", "The password must be at least 10 characters."],
        ["auth.err.mismatch"] = ["Пароли не совпадают.", "Şifreler eşleşmiyor.", "Passwords don’t match."],
        ["auth.err.terms"] = ["Примите условия, чтобы продолжить.", "Devam etmek için koşulları kabul edin.", "Accept the terms to continue."],
        ["auth.expired"] = ["Сеанс завершён. Войдите снова.", "Oturumunuz sona erdi. Lütfen tekrar giriş yapın.", "Your session ended. Please sign in again."],

        // Errors
        ["err.credentials"] = ["Неверная почта или пароль.", "E-posta veya şifre hatalı.", "Email or password is incorrect."],
        ["err.registration"] = ["Эту почту нельзя зарегистрировать: возможно, аккаунт уже существует.", "Bu e-posta ile kayıt yapılamıyor; hesap zaten olabilir.", "This email can’t be registered. It may already have an account."],
        ["err.rateLimited"] = ["Слишком много попыток. Подождите минуту.", "Çok fazla deneme. Lütfen bir dakika bekleyin.", "Too many attempts. Please wait a minute."],
        ["err.deviceLimit"] = ["Достигнут лимит устройств вашего тарифа. Отключите старое устройство на colitu.com/devices или добавьте место в тариф.", "Paketinizin cihaz sınırına ulaşıldı. colitu.com/devices adresinden eski bir cihazı kaldırın veya paketinize cihaz ekleyin.", "Your plan’s device limit is reached. Remove an old device at colitu.com/devices or add a seat to your plan."],
        ["err.region"] = ["Регистрация и вход из вашего региона сейчас недоступны. Если вы используете другой VPN или прокси, отключите его и попробуйте снова.", "Bulunduğunuz bölgeden kayıt ve giriş şu anda kullanılamıyor. Başka bir VPN veya proxy açıksa kapatıp tekrar deneyin.", "Sign-up and sign-in aren’t available from your region right now. If another VPN or proxy is on, turn it off and try again."],
        ["err.trialUsed"] = ["На этом компьютере пробный период уже использован. Выберите тариф на colitu.com, чтобы продолжить.", "Bu bilgisayarda deneme süresi daha önce kullanıldı. Devam etmek için colitu.com’dan bir paket seçin.", "The free trial was already used on this computer. Choose a plan at colitu.com to continue."],
        ["err.notVerified"] = ["Сначала подтвердите адрес электронной почты.", "Önce e-posta adresinizi doğrulayın.", "Please confirm your email address first."],

        // E-mail verification
        ["verify.kicker"] = ["ПОСЛЕДНИЙ ШАГ", "SON ADIM", "ONE LAST STEP"],
        ["verify.title"] = ["Подтвердите почту", "E-postanızı doğrulayın", "Confirm your email"],
        ["verify.sub"] = ["Мы отправили 6-значный код на {email}. Введите его, чтобы активировать аккаунт и бесплатный тариф.", "{email} adresine 6 haneli bir kod gönderdik. Hesabınızı ve ücretsiz planınızı açmak için kodu girin.", "We sent a 6-digit code to {email}. Enter it to activate your account and free plan."],
        ["verify.code"] = ["Код из письма", "E-postadaki kod", "Code from the email"],
        ["verify.submit"] = ["Подтвердить", "Doğrula", "Confirm"],
        ["verify.resend"] = ["Отправить код ещё раз", "Kodu tekrar gönder", "Send the code again"],
        ["verify.resendIn"] = ["Отправить снова через {n} с", "{n} sn sonra tekrar gönderebilirsiniz", "Send again in {n}s"],
        ["verify.sent"] = ["Новый код отправлен. Проверьте и папку «Спам».", "Yeni kod gönderildi. Gereksiz (spam) klasörünü de kontrol edin.", "A new code is on its way. Check your spam folder too."],
        ["verify.other"] = ["Войти в другой аккаунт", "Başka bir hesapla giriş yap", "Use a different account"],
        ["verify.hint"] = ["Код действует 15 минут. Письмо не пришло? Проверьте «Спам» или отправьте код ещё раз.", "Kod 15 dakika geçerlidir. E-posta gelmediyse spam klasörüne bakın veya kodu yeniden gönderin.", "The code is valid for 15 minutes. No email? Check spam or send the code again."],
        ["verify.web"] = ["Код можно ввести и на colitu.com: приложение продолжит само, как только почта будет подтверждена.", "Kodu colitu.com üzerinden de girebilirsiniz; doğrulandığında uygulama kendiliğinden devam eder.", "You can also enter the code on colitu.com; the app carries on by itself once your email is confirmed."],
        ["verify.done"] = ["Почта подтверждена. Добро пожаловать в Colitu!", "E-postanız doğrulandı. Colitu’ya hoş geldiniz!", "Email confirmed. Welcome to Colitu!"],
        ["verify.err.length"] = ["Введите все 6 цифр кода.", "Kodun 6 hanesini de girin.", "Enter all 6 digits of the code."],
        ["verify.err.invalid"] = ["Неверный код. Проверьте письмо и попробуйте снова.", "Kod hatalı. E-postayı kontrol edip tekrar deneyin.", "That code isn’t right. Check the email and try again."],
        ["verify.err.expired"] = ["Срок действия кода истёк. Отправьте новый.", "Kodun süresi doldu. Yeni bir kod isteyin.", "The code has expired. Request a new one."],
        ["verify.err.wait"] = ["Новый код можно запросить через минуту.", "Yeni kodu bir dakika sonra isteyebilirsiniz.", "You can request a new code in a minute."],
        ["verify.err.mail"] = ["Сейчас не удаётся отправить письмо. Попробуйте чуть позже или напишите в поддержку.", "Şu anda e-posta gönderilemiyor. Biraz sonra tekrar deneyin veya destekle iletişime geçin.", "We can’t send email right now. Try again shortly or contact support."],

        // Server categories
        ["cat.all"] = ["Все", "Tümü", "All"],
        ["cat.streaming"] = ["Стриминг", "Streaming", "Streaming"],
        ["cat.gaming"] = ["Игры", "Oyun", "Gaming"],
        ["cat.privacy"] = ["Приватность", "Gizlilik", "Privacy"],
        ["cat.speed"] = ["Скорость", "Hız", "Speed"],
        ["cat.torrent"] = ["Торренты", "Torrent", "Torrent"],
        ["cat.ai"] = ["ИИ", "Yapay zekâ", "AI"],
        ["cat.adblock"] = ["Блокировка рекламы", "Reklam engelleme", "Ad blocking"],
        ["service.youtube_adfree"] = ["YouTube без рекламы", "Reklamsız YouTube", "Ad-free YouTube"],
        ["cat.empty"] = ["В этой категории пока нет серверов.", "Bu kategoride henüz sunucu yok.", "No servers in this category yet."],

        // Live support
        ["nav.support"] = ["Поддержка", "Destek", "Support"],
        ["support.kicker"] = ["ПОДДЕРЖКА", "DESTEK", "SUPPORT"],
        ["support.title"] = ["Живая поддержка", "Canlı destek", "Live support"],
        ["support.sub"] = ["Напишите нам прямо из приложения. Отвечает живой человек, обычно в течение часа.", "Bize doğrudan uygulamadan yazın. Gerçek bir kişi, genellikle bir saat içinde yanıt verir.", "Write to us right from the app. A real person answers, usually within an hour."],
        ["support.new"] = ["Новое обращение", "Yeni talep", "New request"],
        ["support.empty"] = ["Обращений пока нет. Опишите проблему — мы поможем.", "Henüz talebiniz yok. Sorununuzu anlatın, yardımcı olalım.", "No requests yet. Tell us what’s wrong and we’ll help."],
        ["support.pick"] = ["Выберите обращение слева или создайте новое.", "Soldan bir talep seçin veya yeni bir talep oluşturun.", "Pick a request on the left or start a new one."],
        ["support.subject"] = ["Тема", "Konu", "Subject"],
        ["support.subjectHint"] = ["Например: не подключается к серверу", "Örn: Sunucuya bağlanamıyorum", "For example: can’t connect to a server"],
        ["support.message"] = ["Сообщение", "Mesaj", "Message"],
        ["support.messageHint"] = ["Опишите, что происходит и что вы уже пробовали…", "Ne olduğunu ve neler denediğinizi yazın…", "Describe what’s happening and what you’ve tried…"],
        ["support.reply"] = ["Напишите ответ…", "Yanıtınızı yazın…", "Write a reply…"],
        ["support.send"] = ["Отправить", "Gönder", "Send"],
        ["support.attach"] = ["Прикрепить файл", "Dosya ekle", "Attach a file"],
        ["support.fileTypes"] = ["Изображения, PDF, текст, архивы", "Görseller, PDF, metin, arşivler", "Images, PDF, text, archives"],
        ["support.attachHint"] = ["Скриншоты, PDF, TXT, ZIP · до 10 МБ", "Ekran görüntüsü, PDF, TXT, ZIP · en fazla 10 MB", "Screenshots, PDF, TXT, ZIP · up to 10 MB"],
        ["support.diagnostics"] = ["Приложить диагностику", "Tanılama bilgisini ekle", "Include diagnostics"],
        ["support.diagnosticsHint"] = ["Версия приложения и Windows, состояние подключения, последние ошибки и журнал. Без истории посещений.", "Uygulama ve Windows sürümü, bağlantı durumu, son hatalar ve günlük kaydı. Gezinme geçmişi gönderilmez.", "App and Windows version, connection state, recent errors and the log. No browsing history."],
        ["support.create"] = ["Отправить обращение", "Talebi gönder", "Send request"],
        ["support.cancel"] = ["Отмена", "Vazgeç", "Cancel"],
        ["support.you"] = ["Вы", "Siz", "You"],
        ["support.team"] = ["Поддержка Colitu", "Colitu Destek", "Colitu Support"],
        ["support.status.waiting"] = ["ЖДЁТ ОТВЕТА", "YANIT BEKLİYOR", "AWAITING REPLY"],
        ["support.status.open"] = ["В РАБОТЕ", "İNCELENİYOR", "IN PROGRESS"],
        ["support.status.resolved"] = ["РЕШЕНО", "ÇÖZÜLDÜ", "RESOLVED"],
        ["support.status.closed"] = ["ЗАКРЫТО", "KAPANDI", "CLOSED"],
        ["support.closed"] = ["Обращение закрыто. Создайте новое, если нужна помощь.", "Bu talep kapatıldı. Yardım gerekirse yeni bir talep açın.", "This request is closed. Start a new one if you need help."],
        ["support.newReply"] = ["Новый ответ поддержки", "Destekten yeni yanıt", "New reply from support"],
        ["support.sent"] = ["Обращение отправлено. Мы ответим здесь и по почте.", "Talebiniz gönderildi. Buradan ve e-postayla yanıt vereceğiz.", "Request sent. We’ll answer here and by email."],
        ["support.err.subject"] = ["Укажите тему и сообщение.", "Konu ve mesajı yazın.", "Add a subject and a message."],
        ["support.err.file"] = ["Файл больше 10 МБ или такой тип не поддерживается.", "Dosya 10 MB’tan büyük veya bu tür desteklenmiyor.", "The file is over 10 MB or the type isn’t supported."],
        ["support.err.files"] = ["Можно прикрепить не больше 5 файлов.", "En fazla 5 dosya ekleyebilirsiniz.", "You can attach up to 5 files."],
        ["support.err.unavailable"] = ["Поддержка в приложении временно недоступна. Напишите на support@colitu.com.", "Uygulama içi destek geçici olarak kapalı. support@colitu.com adresine yazın.", "In-app support is unavailable right now. Write to support@colitu.com."],
        ["support.help"] = ["Справочный центр", "Yardım merkezi", "Help centre"],
        ["err.noPlan"] = ["Тариф не активен. Выберите тариф, чтобы подключиться.", "Paketiniz aktif değil. Bağlanmak için bir paket seçin.", "Your plan isn’t active. Choose a plan to connect."],
        ["err.quota"] = ["Лимит трафика на этот период исчерпан.", "Bu dönemin trafik kotası doldu.", "You’ve used this period’s traffic allowance."],
        ["err.noServers"] = ["Сейчас нет доступных серверов. Попробуйте чуть позже.", "Şu anda uygun sunucu yok. Biraz sonra tekrar deneyin.", "No server is available right now. Please try again shortly."],
        ["err.network"] = ["Нет связи с сервером Colitu. Проверьте интернет.", "Colitu sunucusuna ulaşılamadı. İnternet bağlantınızı kontrol edin.", "Can’t reach Colitu. Check your internet connection."],
        ["err.refresh"] = ["Не удалось обновить сеанс. Проверьте интернет и попробуйте ещё раз.", "Oturum yenilenemedi. İnternet bağlantınızı kontrol edip tekrar deneyin.", "Couldn’t refresh your session. Check your internet connection and try again."],
        ["err.unreachable"] = ["Сервер не отвечает. Попробуйте другую локацию.", "Sunucu yanıt vermiyor. Başka bir konum deneyin.", "The server isn’t responding. Try another location."],
        ["err.admin"] = ["Для VPN нужны права администратора. Перезапустите Colitu от имени администратора.", "VPN için yönetici izni gerekiyor. Colitu’yu yönetici olarak yeniden başlatın.", "The VPN needs administrator rights. Restart Colitu as administrator."],
        ["err.core"] = ["Файлы VPN повреждены. Переустановите Colitu.", "VPN dosyaları eksik. Colitu’yu yeniden kurun.", "VPN files are missing. Please reinstall Colitu."],
        ["err.port"] = ["Локальный порт занят другой программой VPN или прокси. Закройте её и повторите.", "Yerel bağlantı noktası başka bir VPN/proxy programı tarafından kullanılıyor. Kapatıp tekrar deneyin.", "Another VPN or proxy app is using the local port. Close it and try again."],
        ["err.tun"] = ["Не удалось включить адаптер TUN. Попробуйте режим «Прокси».", "TUN bağdaştırıcısı açılamadı. Proxy modunu deneyin.", "The TUN adapter didn’t start. Try Proxy mode."],
        ["err.generic"] = ["Что-то пошло не так. Попробуйте ещё раз.", "Bir şeyler ters gitti. Tekrar deneyin.", "Something went wrong. Please try again."],
        ["err.payment"] = ["Не удалось создать платёж. Попробуйте другой способ оплаты.", "Ödeme oluşturulamadı. Başka bir ödeme yöntemi deneyin.", "The payment couldn’t be created. Try another payment method."],
        ["err.reconnectFailed"] = ["Соединение потеряно. Нажмите «Подключить», чтобы восстановить.", "Bağlantı koptu. Yeniden bağlanmak için “Bağlan”a basın.", "The connection dropped. Press Connect to restore it."],
        ["info.reconnected"] = ["Соединение восстановлено", "Bağlantı yeniden kuruldu", "Connection restored"],
        ["info.disconnected"] = ["VPN отключён", "VPN bağlantısı kesildi", "VPN disconnected"],
        ["warn.noInternet"] = ["Похоже, пропал интернет. VPN восстановится сам, как только связь вернётся.", "İnternet bağlantınız kesilmiş görünüyor. Bağlantı gelince VPN kendiliğinden devam edecek.", "Your internet connection seems to be down. The VPN will resume on its own once it is back."],
        ["warn.otherVpn"] = ["Похоже, запущен другой VPN (например, Clash). Если соединение не работает, отключите его.", "Başka bir VPN uygulaması (ör. Clash) açık görünüyor. Bağlantı çalışmazsa onu kapatın.", "Another VPN app (for example Clash) seems to be running. If the connection doesn’t work, turn it off."],
        ["warn.proxyOtherUser"] = ["Приложение запущено от имени другой учётной записи Windows, поэтому прокси не защитит ваши программы. Используется режим TUN.", "Uygulama başka bir Windows hesabıyla çalışıyor; proxy sizin uygulamalarınızı korumaz. TUN modu kullanılıyor.", "The app runs as another Windows account, so the proxy would not protect your apps. Using TUN mode instead."],
        ["info.killSwitch"] = ["Соединение прервалось. Kill switch заблокировал интернет, переподключаемся…", "Bağlantı koptu. Kill switch interneti engelledi, yeniden bağlanılıyor…", "Connection lost. The kill switch blocked the internet while we reconnect…"],
        ["err.killSwitch"] = ["Не удалось включить kill switch. Перезапустите Colitu от имени администратора.", "Kill switch açılamadı. Colitu’yu yönetici olarak yeniden başlatın.", "The kill switch couldn’t be turned on. Restart Colitu as administrator."],

        // Kill-switch service (2.6.0)
        ["warn.killSwitchFallback"] = ["Служба kill switch Colitu не запущена: защита работает только пока открыто приложение. Переустановите Colitu, чтобы она защищала и при сбое.", "Colitu kill switch hizmeti çalışmıyor: koruma yalnızca uygulama açıkken çalışır. Çökmelerde de korunmak için Colitu’yu yeniden kurun.", "The Colitu kill-switch service isn’t running, so protection only works while the app is open. Reinstall Colitu to stay protected if it crashes."],
        ["warn.killSwitchProxy"] = ["Kill switch включён в режиме прокси: программы, которые не используют системный прокси, остаются без интернета, пока VPN подключён.", "Kill switch proxy modunda açık: sistem proxy’sini kullanmayan uygulamalar VPN bağlıyken internete çıkamaz.", "The kill switch is on in proxy mode: apps that don’t use the system proxy have no internet while you’re protected."],
        ["info.killSwitchHeld"] = ["Colitu закрылся во время подключения, и kill switch не пускал трафик мимо VPN. Переподключаемся…", "Colitu bağlıyken kapandı; kill switch VPN dışı trafiği engelledi. Yeniden bağlanılıyor…", "Colitu closed while connected, so the kill switch kept traffic from leaving outside the VPN. Reconnecting…"],
        ["info.killSwitchRecovered"] = ["Colitu закрылся во время подключения, поэтому kill switch блокировал интернет, чтобы ничего не утекло. Интернет снова открыт.", "Colitu bağlıyken kapandığı için kill switch hiçbir şey sızmasın diye interneti engelledi. İnternet yeniden açık.", "Colitu closed while connected, so the kill switch blocked the internet to keep anything from leaking. The internet is open again."],
        ["home.sub.blockedCrash"] = ["Colitu закрылся во время подключения, и kill switch не пускает трафик мимо VPN. Переподключаемся автоматически — или отключите защиту.", "Colitu bağlıyken kapandı; kill switch VPN dışı trafiği engelliyor. Otomatik olarak yeniden bağlanıyoruz; isterseniz korumayı kapatın.", "Colitu closed while connected, and the kill switch is keeping traffic from leaving outside the VPN. We’re reconnecting automatically, or you can turn protection off."],
        ["settings.killSwitchLan"] = ["Локальная сеть при kill switch", "Kill switch açıkken yerel ağ", "Local network with the kill switch"],
        ["settings.killSwitchLanHint"] = ["Принтеры, телевизоры и страница роутера остаются доступны, пока kill switch блокирует интернет.", "Kill switch interneti engellerken yazıcılar, TV’ler ve modem sayfası erişilebilir kalır.", "Printers, TVs and the router page stay reachable while the kill switch blocks the internet."],

        // TUN by default and proxy coverage (2.6.0)
        ["tun.prompt.title"] = ["Защищать весь компьютер?", "Tüm bilgisayar korunsun mu?", "Protect the whole computer?"],
        ["tun.prompt.body"] = ["Сейчас Colitu работает в режиме прокси: он защищает только программы, которые используют системный прокси. DNS-запросы, UDP/WebRTC, IPv6 и программы, которые игнорируют прокси, идут мимо VPN. Режим «Весь трафик (TUN)» защищает всё. Переключить?", "Colitu şu an proxy modunda: yalnızca sistem proxy’sini kullanan uygulamaları korur. DNS sorguları, UDP/WebRTC, IPv6 ve proxy’yi yok sayan uygulamalar VPN dışından gider. “Tüm trafik (TUN)” modu her şeyi korur. Geçilsin mi?", "Colitu runs in proxy mode, which only protects apps that use the system proxy. DNS lookups, UDP/WebRTC, IPv6 and apps that ignore the proxy go outside the VPN. “All traffic (TUN)” protects everything. Switch now?"],
        ["tun.prompt.switch"] = ["Перейти на весь трафик (TUN)", "Tüm trafiğe (TUN) geç", "Switch to all traffic (TUN)"],
        ["tun.prompt.keep"] = ["Оставить режим прокси", "Proxy modunda kal", "Keep proxy mode"],
        ["proxy.coverage"] = ["Режим прокси не защищает DNS-запросы, UDP/WebRTC, IPv6 и программы, которые игнорируют системный прокси.", "Proxy modu DNS sorgularını, UDP/WebRTC’yi, IPv6’yı ve sistem proxy’sini yok sayan uygulamaları korumaz.", "Proxy mode doesn’t protect DNS lookups, UDP/WebRTC, IPv6 or apps that ignore the system proxy."],
        ["proxy.chip"] = ["Режим прокси: защита неполная", "Proxy modu: koruma kısmi", "Proxy mode: partial protection"],

        // Two-factor sign-in (2.6.0)
        ["mfa.kicker"] = ["ДВУХФАКТОРНАЯ ЗАЩИТА", "İKİ ADIMLI DOĞRULAMA", "TWO-FACTOR AUTHENTICATION"],
        ["mfa.title"] = ["Введите код", "Kodu girin", "Enter your code"],
        ["mfa.sub"] = ["Откройте приложение-аутентификатор и введите 6-значный код для {email}.", "Doğrulama uygulamanızı açın ve {email} için 6 haneli kodu girin.", "Open your authenticator app and enter the 6-digit code for {email}."],
        ["mfa.subRecovery"] = ["Введите один из резервных кодов, сохранённых при включении двухфакторной защиты. Каждый код действует один раз.", "İki adımlı doğrulamayı açarken kaydettiğiniz kurtarma kodlarından birini girin. Her kod bir kez kullanılabilir.", "Enter one of the recovery codes you saved when you turned on two-factor authentication. Each code works once."],
        ["mfa.code"] = ["Код из приложения", "Uygulamadaki kod", "Code from the app"],
        ["mfa.recoveryCode"] = ["Резервный код", "Kurtarma kodu", "Recovery code"],
        ["mfa.useRecovery"] = ["Использовать резервный код", "Kurtarma kodu kullan", "Use a recovery code"],
        ["mfa.useApp"] = ["Ввести код из приложения", "Uygulamadaki kodu gir", "Use the code from the app"],
        ["mfa.submit"] = ["Подтвердить и войти", "Doğrula ve giriş yap", "Verify and sign in"],
        ["mfa.back"] = ["Вернуться ко входу", "Girişe dön", "Back to sign in"],
        ["mfa.hint"] = ["Двухфакторная защита включается и отключается на colitu.com.", "İki adımlı doğrulama colitu.com üzerinden açılıp kapatılır.", "Two-factor authentication is turned on and off at colitu.com."],
        ["mfa.err.format"] = ["Введите все 6 цифр кода.", "Kodun 6 hanesini de girin.", "Enter all 6 digits of the code."],
        ["mfa.err.recoveryFormat"] = ["Введите резервный код полностью.", "Kurtarma kodunu eksiksiz girin.", "Enter the whole recovery code."],
        ["mfa.err.invalid"] = ["Неверный код. Проверьте приложение и попробуйте снова.", "Kod hatalı. Uygulamayı kontrol edip tekrar deneyin.", "That code isn’t right. Check the app and try again."],
        ["mfa.err.expired"] = ["Время на ввод кода истекло. Войдите снова.", "Kod girme süresi doldu. Lütfen tekrar giriş yapın.", "The time to enter the code ran out. Please sign in again."],
        ["mfa.err.update"] = ["Для этого аккаунта нужна новая версия Colitu. Обновите приложение.", "Bu hesap için Colitu’nun yeni sürümü gerekiyor. Uygulamayı güncelleyin.", "This account needs a newer version of Colitu. Please update the app."],

        // Trial end (2.6.0)
        ["trial.ends"] = ["Пробный период закончится через {days}.", "Deneme sürenizin bitmesine {days} kaldı.", "Your trial ends in {days}."],
        ["trial.freePlan"] = ["Бесплатный", "Ücretsiz", "free"],
        ["trial.move"] = ["Затем вы перейдёте на тариф «{plan}»", "Ardından {plan} planına geçeceksiniz", "You’ll move to the {plan} plan"],
        ["trial.moveDetails"] = ["Затем вы перейдёте на тариф «{plan}» ({details})", "Ardından {plan} planına geçeceksiniz ({details})", "You’ll move to the {plan} plan ({details})"],
        ["trial.gbMonth"] = ["{gb} ГБ в месяц", "ayda {gb} GB", "{gb} GB a month"],
        ["trial.paused"] = ["устройство, которым вы пользовались последним, останется активным, остальные будут приостановлены (без выхода из аккаунта)", "en son kullandığınız cihaz aktif kalır, diğerleri duraklatılır (oturumları kapanmaz)", "the device you used most recently stays active, the others are paused (not signed out)"],
        ["err.disposableEmail"] = ["Временные адреса электронной почты не принимаются. Используйте постоянный адрес (например, Gmail, Outlook, Яндекс).", "Geçici e-posta adresleri kabul edilmiyor. Lütfen kalıcı bir e-posta adresi kullanın (ör. Gmail, Outlook, Yandex).", "Temporary e-mail addresses are not accepted. Please use a permanent address (e.g. Gmail, Outlook, Yandex)."],
        ["err.passwordBreached"] = ["Этот пароль встречается в известной утечке данных. Выберите другой пароль.", "Bu şifre bilinen bir veri sızıntısında yer alıyor. Lütfen başka bir şifre seçin.", "This password appears in a known data breach. Please choose a different one."],
        ["err.signupIpLimit"] = ["С этой сети недавно создано слишком много аккаунтов. Попробуйте позже.", "Bu ağdan kısa sürede çok fazla hesap oluşturuldu. Lütfen daha sonra tekrar deneyin.", "Too many accounts were created from this network recently. Please try again later."],
        ["auth.registerEmailHint"] = ["Временные (одноразовые) адреса электронной почты не принимаются.", "Geçici (tek kullanımlık) e-posta adresleri kabul edilmez.", "Temporary (disposable) e-mail addresses are not accepted."],
        ["mfa.loginEmailTitle"] = ["Это вы входите в аккаунт?", "Bu giriş siz misiniz?", "Is this you signing in?"],
        ["mfa.codeEmail"] = ["Код из письма", "E-postadaki kod", "Code from the e-mail"],
        ["mfa.loginEmailBody"] = ["Вы входите из необычного места. Введите 6-значный код, который мы отправили на вашу почту.", "Alışılmadık bir konumdan giriş yapıyorsunuz. E-posta adresinize gönderdiğimiz 6 haneli kodu girin.", "You are signing in from an unusual location. Enter the 6-digit code we sent to your e-mail."],
        ["mfa.attemptsLeft"] = ["Осталось попыток: {n}.", "Kalan deneme: {n}.", "Attempts left: {n}."],
        ["account.paused"] = ["ПРИОСТАНОВЛЕНО", "DURAKLATILDI", "PAUSED"],
        ["account.activate"] = ["Активировать", "Etkinleştir", "Activate"],
        ["account.activated"] = ["Устройство «{name}» снова активно.", "“{name}” yeniden aktif.", "“{name}” is active again."],
        ["account.security"] = ["Двухфакторная защита", "İki adımlı doğrulama", "Two-factor authentication"],
        ["trial.dismiss"] = ["Скрыть до завтра", "Yarına kadar gizle", "Hide until tomorrow"],
        ["trial.keep"] = ["Сохранить безлимит", "Sınırsızı koru", "Keep unlimited"],

        // Paused device (2.6.0)
        ["paused.kicker"] = ["УСТРОЙСТВО ПРИОСТАНОВЛЕНО", "CİHAZ DURAKLATILDI", "DEVICE PAUSED"],
        ["paused.title"] = ["Это устройство приостановлено", "Bu cihaz duraklatıldı", "This device is paused"],
        ["paused.body"] = ["Ваш тариф позволяет {limit}.", "Paketiniz {limit} kullanımına izin veriyor.", "Your plan allows {limit}."],
        ["paused.bodyNoLimit"] = ["На вашем тарифе уже используется максимум устройств.", "Paketinizdeki cihaz sınırı dolu.", "Your plan’s devices are all in use."],
        ["paused.active"] = ["Активно: {names}.", "Aktif: {names}.", "Active: {names}."],
        ["paused.short"] = ["Это устройство приостановлено: на вашем тарифе уже используется максимум устройств.", "Bu cihaz duraklatıldı: paketinizin cihaz sınırı dolu.", "This device is paused: your plan’s devices are all in use."],
        ["paused.useThis"] = ["Использовать это устройство", "Bunun yerine bu cihazı kullan", "Use this device instead"],
        ["paused.useThisHint"] = ["Другое устройство будет приостановлено вместо этого.", "Onun yerine diğer cihaz duraklatılır.", "The other device is paused in its place."],
        ["paused.premium"] = ["Получить Premium", "Premium’a geç", "Get Premium"],
        ["paused.signOut"] = ["Выйти из аккаунта", "Çıkış yap", "Sign out"],
        ["paused.noDeviceId"] = ["Это устройство ещё не добавлено в аккаунт. Отключите другое устройство на colitu.com и попробуйте снова.", "Bu cihaz henüz hesaba eklenmedi. colitu.com’da başka bir cihazı kaldırıp tekrar deneyin.", "This device isn’t on the account yet. Remove another device at colitu.com and try again."],
        ["paused.notice"] = ["Это устройство приостановлено: тариф позволяет меньше устройств. VPN отключён.", "Bu cihaz duraklatıldı: paketiniz daha az cihaza izin veriyor. VPN kapatıldı.", "This device is paused because your plan allows fewer devices. The VPN is off."],
        ["paused.activated"] = ["Теперь это устройство активно.", "Bu cihaz artık aktif.", "This device is active now."],

        // Split tunnelling (2.6.0)
        ["split.title"] = ["Раздельное туннелирование", "Bölünmüş tünel", "Split tunneling"],
        ["split.hint"] = ["Выберите программы, сайты и IP-адреса, которые идут мимо VPN — или только они идут через VPN. Режим приватности работает отдельно.", "VPN dışından gidecek uygulamaları, siteleri ve IP adreslerini seçin; ya da yalnızca bunlar VPN’den geçsin. Gizlilik modu bundan bağımsızdır.", "Choose apps, sites and IP addresses that skip the VPN, or the only ones that use it. Privacy mode works separately."],
        ["split.mode.off"] = ["Выкл.", "Kapalı", "Off"],
        ["split.mode.bypass"] = ["Выбранное мимо VPN", "Seçilenler VPN dışında", "Selected bypass VPN"],
        ["split.mode.only"] = ["Только выбранное через VPN", "Yalnızca seçilenler VPN’de", "Only selected use VPN"],
        ["split.mode.bypassHint"] = ["Выбранные программы и сайты идут мимо VPN, всё остальное — через VPN.", "Seçilen uygulamalar ve siteler VPN dışından, geri kalan her şey VPN’den gider.", "Selected apps and sites bypass the VPN; everything else uses it."],
        ["split.mode.onlyHint"] = ["Через VPN идут только выбранные программы и сайты, всё остальное — напрямую.", "Yalnızca seçilen uygulamalar ve siteler VPN’den, geri kalan her şey doğrudan gider.", "Only the selected apps and sites use the VPN; everything else goes direct."],
        ["split.apps"] = ["Программы", "Uygulamalar", "Apps"],
        ["split.sites"] = ["Сайты", "Siteler", "Sites"],
        ["split.ips"] = ["IP-адреса и подсети", "IP adresleri ve ağlar", "IP addresses and ranges"],
        ["split.addApp"] = ["Добавить программу…", "Uygulama ekle…", "Add app…"],
        ["split.add"] = ["Добавить", "Ekle", "Add"],
        ["split.remove"] = ["Убрать", "Kaldır", "Remove"],
        ["split.domainHint"] = ["example.com (вместе с поддоменами)", "example.com (alt alan adlarıyla)", "example.com (subdomains included)"],
        ["split.ipHint"] = ["203.0.113.5 или 198.51.100.0/24", "203.0.113.5 veya 198.51.100.0/24", "203.0.113.5 or 198.51.100.0/24"],
        ["split.err.domain"] = ["Введите адрес сайта, например example.com.", "Bir site adresi girin, ör. example.com.", "Enter a site address such as example.com."],
        ["split.err.ip"] = ["Введите IP-адрес или подсеть, например 198.51.100.0/24.", "Bir IP adresi veya ağ girin, ör. 198.51.100.0/24.", "Enter an IP address or range such as 198.51.100.0/24."],
        ["split.err.app"] = ["Выберите файл программы (.exe).", "Bir uygulama dosyası (.exe) seçin.", "Choose a program file (.exe)."],
        ["split.err.limit"] = ["Список заполнен.", "Liste dolu.", "The list is full."],
        ["split.proxyNote"] = ["В режиме прокси программы разделить нельзя: прокси не различает программы. Работают сайты и IP-адреса; для программ включите режим «Весь трафик (TUN)».", "Proxy modunda uygulamalar ayrılamaz: proxy uygulamaları ayırt edemez. Siteler ve IP adresleri çalışır; uygulamalar için “Tüm trafik (TUN)” modunu açın.", "Apps can’t be split in proxy mode: a proxy can’t tell apps apart. Sites and IP addresses work; switch to “All traffic (TUN)” for apps."],
        ["split.empty"] = ["Пока ничего не добавлено.", "Henüz bir şey eklenmedi.", "Nothing added yet."],
        ["split.chip.bypass"] = ["Раздельное туннелирование: мимо VPN — {n}", "Bölünmüş tünel: VPN dışında {n}", "Split tunneling on: {n} outside VPN"],
        ["split.chip.only"] = ["Раздельное туннелирование: через VPN только {n}", "Bölünmüş tünel: yalnızca {n} VPN’de", "Split tunneling on: only {n} use VPN"],
        ["entry.one"] = ["{n} программа или сайт", "{n} uygulama/site", "{n} app or site"],
        ["entry.few"] = ["{n} программы или сайта", "{n} uygulama/site", "{n} apps/sites"],
        ["entry.many"] = ["{n} программ или сайтов", "{n} uygulama/site", "{n} apps/sites"],

        // Multihop routes and rotating IP (2.6.0)
        ["multihop.section"] = ["Мультихоп", "Çoklu atlama", "Multihop"],
        ["multihop.sectionHint"] = ["Трафик идёт через два сервера: сначала входной, затем выходной. Работает только по VLESS; пинг показан до входного сервера.", "Trafik iki sunucudan geçer: önce giriş, sonra çıkış. Yalnızca VLESS ile çalışır; ping yalnızca giriş sunucusu içindir.", "Traffic passes two servers: the entry first, then the exit. Works over VLESS only; ping is measured to the entry server."],
        ["multihop.ping"] = ["Оценка · +1 узел", "Tahmini · +1 atlama", "Estimated · +1 hop"],
        ["multihop.sub"] = ["Двойной VPN · {entry} → {exit}", "Çift VPN · {entry} → {exit}", "Double VPN · {entry} → {exit}"],
        ["multihop.home"] = ["Вход {entry} → выход {exit}", "Giriş {entry} → çıkış {exit}", "Entry {entry} → Exit {exit}"],
        ["multihop.gone"] = ["Этот маршрут больше недоступен. Список обновлён — выберите другой.", "Bu rota artık kullanılamıyor. Liste yenilendi; başka bir tane seçin.", "This route is no longer available. The list was refreshed; pick another one."],
        ["multihop.needsVless"] = ["Мультихоп работает только по VLESS, а этот маршрут не предлагает такой транспорт.", "Çoklu atlama yalnızca VLESS ile çalışır, bu rota böyle bir aktarım sunmuyor.", "Multihop works over VLESS only, and this route offers no VLESS transport."],
        ["rotation.title"] = ["Меняющийся IP", "Dönen IP", "Rotating IP"],
        ["rotation.hint"] = ["IP-адрес и страна выхода меняются по расписанию без отключения от VPN. Уже открытые соединения остаются на прежнем выходе. Работает только по VLESS.", "Çıkış IP adresiniz ve ülkeniz bağlantı kesilmeden belirli aralıklarla değişir. Açık bağlantılar eski çıkışta kalır. Yalnızca VLESS ile çalışır.", "Your exit IP address and country change on a schedule without disconnecting. Open connections stay on their old exit. Works over VLESS only."],
        ["rotation.off"] = ["Выкл.", "Kapalı", "Off"],
        ["rotation.minutes"] = ["{n} мин", "{n} dk", "{n} min"],
        ["rotation.countries"] = ["Страны выхода", "Çıkış ülkeleri", "Exit countries"],
        ["rotation.countriesHint"] = ["Отметьте минимум две страны. Россия не входит в набор по умолчанию.", "En az iki ülke işaretleyin. Rusya varsayılan sette yer almaz.", "Tick at least two countries. Russia is not in the default set."],
        ["rotation.err.few"] = ["Отметьте минимум две страны.", "En az iki ülke işaretleyin.", "Tick at least two countries."],
        ["rotation.needsVless"] = ["Смена IP работает только по VLESS, а этот сервер не предлагает такой транспорт.", "IP değişimi yalnızca VLESS ile çalışır, bu sunucu böyle bir aktarım sunmuyor.", "Rotation needs VLESS, and this server offers no VLESS transport."],
        ["rotation.home"] = ["Выход: {exit} · смена через {time}", "Çıkış: {exit} · değişimine {time}", "Exit: {exit} · changes in {time}"],
        ["rotation.homeDue"] = ["Выход: {exit} · меняется сейчас", "Çıkış: {exit} · şimdi değişiyor", "Exit: {exit} · changing now"],
        ["account.manualConfig"] = ["Ручная настройка", "Manuel yapılandırma", "Manual configuration"],

        // Tray
        ["tray.open"] = ["Открыть Colitu", "Colitu’yu aç", "Open Colitu"],
        ["tray.connect"] = ["Подключить", "Bağlan", "Connect"],
        ["tray.disconnect"] = ["Отключить", "Bağlantıyı kes", "Disconnect"],
        ["tray.exit"] = ["Выйти из Colitu", "Colitu’dan çık", "Quit Colitu"],
        ["tray.hidden"] = ["Colitu работает в трее. Защита остаётся включённой.", "Colitu tepside çalışıyor. Koruma açık kalır.", "Colitu is still running in the tray. Protection stays on."],
    };
}

/// <summary>XAML: <c>Text="{s:T home.connect}"</c> — follows language changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }

    public T(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// Colitu's own name of a panel transport ("Fast" for Hysteria2 and so on):
/// the technical protocol names stay out of the UI.
/// </summary>
public static class ColituTransportNames
{
    private static readonly HashSet<string> Known = ["hysteria2", "vless-reality", "vless-xhttp", "trojan", "shadowsocks"];

    public static string Of(string? protocol, Loc loc)
    {
        if (string.IsNullOrEmpty(protocol)) return "";
        return loc[Known.Contains(protocol) ? $"transport.{protocol}" : "transport.other"];
    }
}
