namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenExperimental()
    {
        // The control API lists every connection (the sites in use) and can change routing:
        // only open it when statistics need it, and never without a secret.
        if (_config.GuiItem.EnableStatistics || _config.GuiItem.DisplayRealTimeSpeed)
        {
            _coreConfig.experimental ??= new Experimental4Sbox();
            _coreConfig.experimental.clash_api = new Clash_Api4Sbox()
            {
                external_controller = $"{Global.Loopback}:{AppManager.Instance.StatePort2}",
                secret = AppManager.Instance.ClashApiSecret,
            };
        }

        if (_config.CoreBasicItem.EnableCacheFile4Sbox)
        {
            _coreConfig.experimental ??= new Experimental4Sbox();
            _coreConfig.experimental.cache_file = new CacheFile4Sbox()
            {
                enabled = true,
                path = Utils.GetBinPath("cache.db"),
                store_fakeip = context.SimpleDnsItem.FakeIP == true
            };
        }
    }
}
