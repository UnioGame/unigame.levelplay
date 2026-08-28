namespace UniGame.Ads.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Cysharp.Threading.Tasks;
    using GameFlow.Runtime;
    using R3;
    using UniGame.Runtime.ObjectPool.Extensions;
    using UniGame.Runtime.Utils;
    using UnityEngine.Pool;

    [Serializable]
    public class AdsService : GameService, IAdsService
    {
        private string _defaultPlatform;
        private AdsConfiguration _configuration;
        private Dictionary<string,IAdsService> _adsServices = new();
        private Subject<AdsActionData> _adsActionSubject;
        private Dictionary<PlacementType, List<AdsPlacement>> _placementsByType;
        private Dictionary<string,AdsPlacement> _placementsById;
        private AdsDataConfiguration _adsData;
        
        public AdsService(AdsConfiguration configuration)
        {
            _placementsByType = new();
            _placementsById = new();
            
            _adsActionSubject = new Subject<AdsActionData>();
            _adsActionSubject.AddTo(LifeTime);
            
            _configuration = configuration;
            _adsData = configuration.adsData;
            _defaultPlatform = configuration.defaultProvider;

            foreach (var value in EnumValue<PlacementType>.Values)
            {
                _placementsByType[value] = new List<AdsPlacement>();
            }
            
            foreach (var placement in _adsData.placements)
            {
                _placementsById[placement.id] = placement;
                _placementsByType[placement.placementType].Add(placement);
            }
        }

        public bool RewardedAvailable = true;
            // get
            // {
            //     return _adsServices
            //         .Any(x => x.Value.RewardedAvailable);
            // }

        public bool InterstitialAvailable = true;
            // get
            // {
            //     return _adsServices
            //         .Any(x => x.Value.InterstitialAvailable);
            // }

        public Observable<AdsActionData> AdsAction => _adsActionSubject;
        
        public void RegisterAdsProvider(string platform,IAdsService adsService)
        {
            _adsServices[platform] = adsService;
            adsService.AddTo(LifeTime);

            var actionDisposable = adsService.AdsAction
                .Subscribe(_adsActionSubject.OnNext);
            LifeTime.AddDispose(actionDisposable);
        }
        
        public void ValidateIntegration()
        {
            foreach (var adsService in _adsServices)
            {
                adsService.Value.ValidateIntegration();
            }
        }

        public async UniTask<bool> IsPlacementAvailable(string placementName)
        {
            if (_placementsById.TryGetValue(placementName, out var placement) &&
                placement.placementType == PlacementType.Interstitial &&
                !IsInterstitialEnabled())
                return false;

            foreach (var adsService in _adsServices)
            {
                if (placement != null &&
                    placement.placementType == PlacementType.Interstitial &&
                    !IsInterstitialEnabled(adsService.Key))
                    continue;

                var value = adsService.Value;
                var isPlacementAvailable = await value.IsPlacementAvailable(placementName);
                if(isPlacementAvailable) return true;
            }
            return false;
        }
        
        public async UniTask<bool> IsPlacementAvailable(PlacementType placementType)
        {
            foreach (var adsService in _adsServices)
            {
                if (placementType == PlacementType.Interstitial &&
                    !IsInterstitialEnabled(adsService.Key))
                    continue;

                var value = adsService.Value;
                var isPlacementAvailable = await value.IsPlacementAvailable(placementType);
                if(isPlacementAvailable) return true;
            }
            return false;
        }

        public async UniTask LoadAdsAsync()
        {
            var tasks = ListPool<UniTask>.Get();
            
            tasks.AddRange(_adsServices
                .Select(x => x.Value.LoadAdsAsync()));
            await UniTask.WhenAll(tasks);
            
            tasks.Despawn();
        }

        public async UniTask<AdsShowResult> Show(string placement, PlacementType type)
        {
            foreach (var adsService in _adsServices)
            {
                if (type == PlacementType.Interstitial &&
                    !IsInterstitialEnabled(adsService.Key))
                    continue;

                var value = adsService.Value;
                var isAvailable = await value.IsPlacementAvailable(placement);
                if(!isAvailable) continue;
                
                var result = await value.Show(placement, type);
                return result;
            }
            
            return new AdsShowResult()
            {
                Error = true,
                Message = $"No ads available for placement: {placement}",
                PlacementType = type,
                PlacementName = placement,
                RewardName = string.Empty,
                RewardAmount = 0,
                Rewarded = false,
            };
        }

        public async UniTask<AdsShowResult> Show(PlacementType type)
        {
            foreach (var service in _adsServices)
            {
                if (type == PlacementType.Interstitial &&
                    !IsInterstitialEnabled(service.Key))
                    continue;

                var adsService = service.Value;
                
                var isAvailable = await adsService.IsPlacementAvailable(type);
                if(!isAvailable) continue;
                
                var result = await adsService.Show(type);
                return result;
            }
            
            return new AdsShowResult()
            {
                Error = true,
                Message = $"No ads available for type: {type}",
                PlacementType = type,
                PlacementName = string.Empty,
                RewardName = string.Empty,
                RewardAmount = 0,
                Rewarded = false,
            };
        }

        public async UniTask<AdsShowResult> ShowRewardedAdAsync(string placementId)
        {
            foreach (var service in _adsServices)
            {
                var adsService = service.Value;
                
                var isAvailable = await adsService.IsPlacementAvailable(placementId);
                if(!isAvailable) continue;
                
                var result = await adsService.ShowRewardedAdAsync(placementId);
                return result;
            }
            
            return new AdsShowResult()
            {
                Error = true,
                Message = $"No ads available for type: {placementId}",
                PlacementType =  PlacementType.Rewarded,
                PlacementName = placementId,
                RewardName = string.Empty,
                RewardAmount = 0,
                Rewarded = false,
            };
        }

        private bool IsInterstitialEnabled(string platform = null)
        {
            return string.IsNullOrEmpty(platform)
                ? _adsServices.Keys.Any(_adsData.IsInterstitialEnabled)
                : _adsData.IsInterstitialEnabled(platform);
        }

        public async UniTask<AdsShowResult> ShowInterstitialAdAsync(string placementId)
        {
            foreach (var service in _adsServices)
            {
                if (!IsInterstitialEnabled(service.Key))
                    continue;

                var adsService = service.Value;
                
                var isAvailable = await adsService.IsPlacementAvailable(placementId);
                if(!isAvailable) continue;
                
                var result = await adsService.ShowInterstitialAdAsync(placementId);
                return result;
            }
            
            return new AdsShowResult()
            {
                Error = true,
                Message = $"No ads available for type: {placementId}",
                PlacementType = PlacementType.Interstitial,
                PlacementName = placementId,
                RewardName = string.Empty,
                RewardAmount = 0,
                Rewarded = false,
            };
        }
    }
}