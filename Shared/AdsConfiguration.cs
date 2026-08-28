namespace UniGame.Ads.Runtime
{
    using System;
    using System.Collections.Generic;
    using Game.Modules.unigame.ads.Shared;
    using GameLib.AdsCore.Config;
    using Sirenix.OdinInspector;
    using UnityEngine;

    [Serializable]
    public class AdsConfiguration
    {
        public bool waitForInitialization = true;
        
        [Header("Placements")]
        [InlineProperty]
        [HideLabel]
        public AdsDataConfiguration adsData;

        [Header("Providers")]
        [FoldoutGroup(nameof(GetProviders))]
        [Tooltip("default ads provider to use")]
        public string defaultProvider = string.Empty;
		
        [ListDrawerSettings(ListElementLabelName = "@adsPlatformName")]
        [SerializeReference]
        public List<AdsProvider> providers =new();
        
        public IEnumerable<string> GetProviders()
        {
            foreach (var provider in providers)
            {
                yield return provider.adsPlatformName;
            }
        }
        
        public Dictionary<string,PlatformAdsPlacement> GetPlatformPlacements(string platform)
        {
            var placements = new Dictionary<string, PlatformAdsPlacement>();
            foreach (var placement in adsData.placements)
            {
                if (placement.placementType == PlacementType.Interstitial &&
                    !adsData.IsInterstitialEnabled(platform))
                    continue;
                foreach (var platformData in placement.placements)
                {
                    if(platformData.platform != platform)
                        continue;
                    
                    var platformPlacement = new PlatformAdsPlacement
                    {
                        id = placement.id,
                        platformPlacement = platformData.placement,
                        placementType = placement.placementType,
                        platform = platformData.platform,
                    };

                    placements[placement.id] = platformPlacement;
                }
            }
            return placements;
        }
    }
}