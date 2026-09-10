namespace UniGame.Ads.Runtime
{
    using System;
    using Cysharp.Threading.Tasks;
    using GameLib.AdsCore.Config;
    using Sirenix.OdinInspector;
    using UniGame.Core.Runtime;
    using UnityEngine;

    [Serializable]
    public class AdMobAdsProvider : AdsProvider
    {
        public override async UniTask<IAdsService> Create(IContext context, AdsConfiguration configuration)
        {
            Debug.Log($"[AdsConsent] provider created: platform={Application.platform}, provider={adsPlatformName}");

            var platformPlacements = configuration
                .GetPlatformPlacements(adsPlatformName);
            var consentService = new AdmobConsentService();
            context.Publish<IAdsConsentService>(consentService);

            var service = new AdmobAdsService(adsPlatformName,configuration.adsData,platformPlacements, consentService);
            context.Publish<IAdsConsentFlow>(service);
            Debug.Log("[AdsConsent] consent flow published");
            return service;
        }
    }
}
