using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniGame.Ads.Runtime
{
    using Core.Runtime;
    using Cysharp.Threading.Tasks;
    using Game.Modules.unigame.ads.Shared;
    using R3;
    using UniCore.Runtime.ProfilerTools;
    using UniGame.Runtime.DataFlow;
    using YandexMobileAds;
    using YandexMobileAds.Base;

    [Serializable]
    public class YandexAdsService : IAdsService
    {
        
        private LifeTime _lifeTime;
        private List<AdsShowResult> _rewardedHistory = new();
        private Dictionary<string, AdsShowResult> _awaitedRewards = new();

        private Dictionary<string, PlatformAdsPlacement> _placements;
        private float _reloadAdsInterval;
        private float _lastAdsReloadTime;
        private bool _loadingAds;
        private bool _isInProgress;
        private bool _rewardedAdReceived;
        private string _activePlacement = string.Empty;

        private Subject<AdsActionData> _adsAction = new();
        private ReactiveCommand<AdsShowResult> _applyRewardedCommand = new();
        
        private RewardedAdLoader _rewardedAdLoader;
        private RewardedAd _rewardedAd;
        private string _platformId;
        private AdsDataConfiguration _adsConfig;

        public ILifeTime LifeTime => _lifeTime;
        
        public bool RewardedAvailable => _rewardedAd != null;

        public bool InterstitialAvailable => throw new NotImplementedException();

        public Observable<AdsActionData> AdsAction => _adsAction;
        
        /// <summary>
        /// placements - dictionary of platform placements
        /// </summary>
        /// <param name="platformId"></param>
        /// <param name="configuration"></param>
        /// <param name="placements"></param>
        public YandexAdsService(string platformId,
            AdsDataConfiguration configuration, 
            Dictionary<string,PlatformAdsPlacement> placements)
        {
            _lifeTime = new ();
            _platformId = platformId;
            _adsConfig = configuration;
            _placements = placements;

            _reloadAdsInterval = configuration.reloadAdsInterval;
            _lastAdsReloadTime = -_reloadAdsInterval;

            Initialize();
        }
        private void Initialize()
        {
            SubscribeToEvents();
            
            _rewardedAdLoader = new RewardedAdLoader();

            var failedLoadDisposable = Observable
                .FromEvent(x => _rewardedAdLoader.OnAdFailedToLoad += YandexRewardAdFailedToLoad,
                x => _rewardedAdLoader.OnAdFailedToLoad -= YandexRewardAdFailedToLoad)
                .Subscribe();

            _lifeTime.AddDispose(failedLoadDisposable);
            
            var rewardedLoaded = Observable
                .FromEvent(x => _rewardedAdLoader.OnAdLoaded += YandexRewardAdLoaded,
                    x => _rewardedAdLoader.OnAdLoaded -= YandexRewardAdLoaded)
                .Subscribe();
            
            _lifeTime.AddDispose(rewardedLoaded);

            LifetimeExtension.AddTo(_applyRewardedCommand
                    .Subscribe(ApplyRewardedCommand), _lifeTime);

            LoadAdsAsync()
                .AttachExternalCancellation(_lifeTime.Token)
                .Forget();
        }

        private void AddPlacementResult(string placeId, PlacementType type, bool rewarded, bool error = false, string message = "")
        {
            var result = new AdsShowResult
            {
                Error = error,
                Message = message,
                PlacementType = type,
                PlacementName = placeId,
                Rewarded = rewarded,
            };

            _awaitedRewards[placeId] = result;
        }

        public UniTask<bool> IsPlacementAvailable(PlacementType placementType)
        {
            switch (placementType)
            {
                case PlacementType.Rewarded:
                    return UniTask.FromResult(RewardedAvailable);
                case PlacementType.Interstitial:
                    return UniTask.FromResult(InterstitialAvailable);
                case PlacementType.Banner:
                    return UniTask.FromResult(false);
                default:
                    return UniTask.FromResult(false);
            }

            return UniTask.FromResult(false);
        }

        public async UniTask<bool> IsPlacementAvailable(string placementName)
        {
            if (!_placements.TryGetValue(placementName, out var placement))
            {
                GameLog.Log($"Yandex Ads Service: haven't ads placement item with name {placementName}", Color.red);
                return false;
            }

            var placementId = placement.platformPlacement;
            
            if (string.IsNullOrEmpty(placementId))
            {
                GameLog.Log($"Yandex Ads Service: have't override placement for {placementName}", Color.red);
                return false;
            }
            
            GameLog.Log($"Yandex Ads Service: have override placement is {placementId}", Color.green);
            
            var placementType = placement.placementType;
            switch (placementType)
            {
                case PlacementType.Rewarded:
                    {
                        GameLog.Log($"Yandex Ads Service: rewarded ads loaded status: {_rewardedAd != null}", Color.yellow);
                        return _rewardedAd != null;
                    }
                case PlacementType.Interstitial:
                    return InterstitialAvailable;
            }

            return false;
        }

        public async UniTask LoadAdsAsync()
        {
            if (_loadingAds || _lifeTime.IsTerminated) return;

            GameLog.Log("Yandex Ads Service: start to load rewarded ads", Color.yellow);
            _loadingAds = true;

            var delay = Time.realtimeSinceStartup - _lastAdsReloadTime;
            delay = delay > _reloadAdsInterval ? 0 : _reloadAdsInterval - delay;
            delay = Mathf.Max(0, delay);

            await UniTask.Delay(TimeSpan.FromSeconds(delay))
                .AttachExternalCancellation(_lifeTime.Token);

            _lastAdsReloadTime = Time.realtimeSinceStartup;

            var rewarded = GetRewardedPlacement();
            
            var adRequestConfiguration = new AdRequestConfiguration
                .Builder(rewarded.platformPlacement)
                .Build();
            
            _rewardedAdLoader.LoadAd(adRequestConfiguration);

            _loadingAds = false;
        }


        public async UniTask<AdsShowResult> Show(string placement, PlacementType type)
        {
            if (!_placements.TryGetValue(placement, out var adsPlacement))
            {
                var message = $"Yandex Ads Service: haven't ads placement item with name {placement}";
                Debug.LogError(message);
                
                return new AdsShowResult()
                {
                    PlacementName = placement,
                    Rewarded = false,
                    Error = true,
                    Message = message,
                    PlacementType = type,
                };
            }
            
            _activePlacement = placement;
            
            GameLog.Log($"Yandex Ads Service: show placement: {placement}. Id: {type}", Color.yellow);

            if (_isInProgress)
            {
                return new AdsShowResult()
                {
                    PlacementName = placement,
                    Rewarded = false,
                    Error = true,
                    Message = AdsMessages.AdsAlreadyInProgress,
                    PlacementType = type,
                };
            }

            _isInProgress = true;
            _rewardedAdReceived = false;
            _activePlacement = placement;

            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = placement,
                Message = string.Empty,
                ActionType = PlacementActionType.Requested,
                PlacementType = type,
            });

            ShowPlacement(placement, type);

            await UniTask
                .WaitWhile(() => _awaitedRewards.ContainsKey(placement) == false)
                .AttachExternalCancellation(_lifeTime.Token);

            var placementResult = _awaitedRewards[placement];

            _awaitedRewards.Remove(placement);
            _isInProgress = false;

            return placementResult;
        }
        
        public void ShowPlacement(string placeId, PlacementType type)
        {
            GameLog.Log($"Yandex Ads Service: show place id: {placeId}. Id: {type}", Color.yellow);

            switch (type)
            {
                case PlacementType.Rewarded:
                    ShowRewardedVideo(placeId);
                    break;
                case PlacementType.Interstitial:
                    throw new NotImplementedException();
                case PlacementType.Banner:
                    throw new NotImplementedException();
            }
        }
        private void ShowRewardedVideo(string placeId)
        {
            var isVideoAvailable = _rewardedAd != null;
            if (isVideoAvailable == false)
            {
                var rewardedResult = new AdsShowResult
                {
                    PlacementName = placeId,
                    Rewarded = false,
                    Error = true,
                    Message = AdsMessages.RewardedUnavailable
                };
                _applyRewardedCommand.Execute(rewardedResult);
                return;
            }

            _rewardedAd.Show();
        }

        public UniTask<AdsShowResult> Show(PlacementType type)
        {
            throw new NotImplementedException();
        }

        public UniTask<AdsShowResult> ShowInterstitialAdAsync(string placeId)
        {
            throw new NotImplementedException();
        }

        public async UniTask<AdsShowResult> ShowRewardedAdAsync(string placeId)
        {
            var showResult = await Show(placeId, PlacementType.Rewarded);
            return showResult;
        }

        private void ApplyRewardedCommand(AdsShowResult result)
        {
            _rewardedHistory.Add(result);

            if (_awaitedRewards.ContainsKey(result.PlacementName))
                return;
            else
                _awaitedRewards.Add(result.PlacementName, result);
        }

        #region ads events
        public void ValidateIntegration()
        {
            throw new NotImplementedException();
        }

        private void SubscribeToEvents()
        {
            LifetimeExtension.AddTo(_adsAction.Subscribe(LoadAdsAction), _lifeTime);
        }
        public void Dispose()
        {
            _lifeTime.Terminate();

            if (_rewardedAd == null)
                return;

            _rewardedAd.OnAdClicked -= YandexRewardAdClickEvent;
            _rewardedAd.OnAdDismissed -= YandexRewardAdDismissedEvent;
            _rewardedAd.OnAdFailedToShow -= YandexRewardAdFailedToShowEvent;
            _rewardedAd.OnAdImpression -= YandexRewardAdImpressionEvent;
            _rewardedAd.OnAdShown -= YandexRewardAdShowEvent;
            _rewardedAd.OnRewarded -= YandexRewardAdRewardedEvent;
        }
        private void SubscribeToAd()
        {
            _rewardedAd.OnAdClicked += YandexRewardAdClickEvent;
            _rewardedAd.OnAdDismissed += YandexRewardAdDismissedEvent;
            _rewardedAd.OnAdFailedToShow += YandexRewardAdFailedToShowEvent;
            _rewardedAd.OnAdImpression += YandexRewardAdImpressionEvent;
            _rewardedAd.OnAdShown += YandexRewardAdShowEvent;
            _rewardedAd.OnRewarded += YandexRewardAdRewardedEvent;
        }
        public void LoadAdsAction(AdsActionData actionData)
        {
            Debug.Log($"[Ads Service] Action: {actionData.PlacementName} {actionData.PlacementType} {actionData.Message} {actionData.ActionType}");
        }
        public void YandexRewardAdLoaded(object sender, RewardedAdLoadedEventArgs args)
        {
            GameLog.Log("Yandex Ads Service: Yandex Reward Ad Loaded", Color.green);
            _rewardedAd = args.RewardedAd;
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = "Ad loadeed",
                ActionType = PlacementActionType.Available,
                PlacementType = PlacementType.Rewarded,
            });
            SubscribeToAd();
        }

        public void YandexRewardAdFailedToLoad(object sender, AdFailedToLoadEventArgs args)
        {
            GameLog.Log($"Yandex Ads Service: Yandex Reward Ad failed to load: {args.Message}", Color.red);
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = args.Message,
                ActionType = PlacementActionType.Failed,
                PlacementType = PlacementType.Rewarded,
            });

            LoadAdsAsync().Forget();
        }
        public void YandexRewardAdClickEvent(object sender, EventArgs args)
        {
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = "Click to reward ad",
                ActionType = PlacementActionType.Clicked,
                PlacementType = PlacementType.Rewarded,
            });
        }

        public void YandexRewardAdShowEvent(object sender, EventArgs args)
        {
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = "Show reward ad",
                ActionType = PlacementActionType.Opened,
                PlacementType = PlacementType.Rewarded,
            });
            LoadAdsAsync().Forget();
        }

        public void YandexRewardAdFailedToShowEvent(object sender, AdFailureEventArgs args)
        {
            CompleteActiveRewardedShowAsFailed(args.Message);
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = args.Message,
                ActionType = PlacementActionType.Failed,
                PlacementType = PlacementType.Rewarded,
            });
            DestroyRewardedAd();
            LoadAdsAsync().Forget();
        }

        public void YandexRewardAdDismissedEvent(object sender, EventArgs args)
        {
            var actionType = _rewardedAdReceived
                ? PlacementActionType.Closed
                : PlacementActionType.Failed;

            if (!_rewardedAdReceived)
                CompleteActiveRewardedShowAsFailed("Rewarded ad was dismissed before reward.");

            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = "Ad dismissed.",
                ActionType = actionType,
                PlacementType = PlacementType.Rewarded,
            });
            DestroyRewardedAd();
            LoadAdsAsync().Forget();
        }

        public void YandexRewardAdImpressionEvent(object sender, ImpressionData impressionData)
        {
            var data = impressionData == null ? "null" : impressionData.rawData;
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = $"HandleImpression event received with data: {data}",
                ActionType = PlacementActionType.Failed,
                PlacementType = PlacementType.Rewarded,
            });
        }

        public void YandexRewardAdRewardedEvent(object sender, Reward args)
        {
            _rewardedAdReceived = true;
            var placementId = _activePlacement;

            var rewardedResult = new AdsShowResult
            {
                PlacementName = placementId,
                Rewarded = true,
                Error = false,
                Message = "Rewarded"
            };

            _applyRewardedCommand.Execute(rewardedResult);

            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = placementId,
                Message = $"Recieve reward with id {placementId}",
                ActionType = PlacementActionType.Rewarded,
                PlacementType = PlacementType.Rewarded,
            });
            LoadAdsAsync().Forget();
        }
        
        #endregion
        
        public void DestroyRewardedAd()
        {
            if (_rewardedAd != null)
            {
                _rewardedAd.Destroy();
                _rewardedAd = null;
            }
        }

        private void CompleteActiveRewardedShowAsFailed(string message)
        {
            if (!_isInProgress || string.IsNullOrEmpty(_activePlacement))
                return;

            AddPlacementResult(
                _activePlacement,
                PlacementType.Rewarded,
                rewarded: false,
                error: true,
                message: message);
            _isInProgress = false;
        }

        #region private methods

        
        private PlatformAdsPlacement GetRewardedPlacement()
        {
            foreach (var platformAdsPlacement in _placements.Values)
            {
                if(platformAdsPlacement.placementType != PlacementType.Rewarded)
                    continue;
                return platformAdsPlacement;
            }

            return default;
        }

        #endregion
    }
}
