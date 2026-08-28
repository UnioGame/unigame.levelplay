namespace UniGame.Ads.Runtime
{
#if ODIN_INSPECTOR
    using Sirenix.OdinInspector;
#endif
    using System;
    using System.Collections.Generic;

    [Serializable]
    public class AdsDataConfiguration
    {
        public float reloadAdsInterval = 30f;

#if ODIN_INSPECTOR
        [BoxGroup("interstitial platforms", ShowLabel = false)]
        [ListDrawerSettings(ListElementLabelName = "@platform.value")]
#endif
        public List<InterstitialPlatformSettings> interstitialPlatforms = new();

#if ODIN_INSPECTOR
        [BoxGroup("placements")]
        [HideLabel]
        [ListDrawerSettings(ListElementLabelName = "@id")]
#endif
        public List<AdsPlacement> placements = new();

        public bool IsInterstitialEnabled(string platform)
        {
            foreach (var settings in interstitialPlatforms)
            {
                if (settings.platform.Equals(platform))
                    return settings.enabled;
            }

            return true;
        }

        public AdsPlacement GetPlatformPlacementByName(string name)
        {
            foreach (var item in placements)
            {
                if (item.id == name)
                {
                    return item;
                }
            }

            return default;
        }
    }

    [Serializable]
    public class InterstitialPlatformSettings
    {
        public AdsPlatformId platform;
        public bool enabled = true;
    }
}