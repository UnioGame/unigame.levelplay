namespace UniGame.Ads.Runtime
{
    using UnityEngine;
    using System;
    using System.Collections.Generic;
    using Cysharp.Threading.Tasks;
    using Game.Modules.unigame.ads.Shared;
    using GoogleMobileAds.Api;
    using R3;
    using UniCore.Runtime.ProfilerTools;
    using UniGame.Core.Runtime;
    using UniGame.Runtime.DataFlow;
    using UniGame.Runtime.Rx;

    [Serializable]
    public class AdmobAdsService : IAdsService
    {
        private const string MediationGroupNameKey = "mediation_group_name";

#if UNITY_ANDROID
        private const string TestInterstitialPlacementId = "ca-app-pub-3940256099942544/1033173712";
        private const string TestRewardedPlacementId =     "ca-app-pub-3940256099942544/5224354917";
#elif UNITY_IPHONE
        private const string TestInterstitialPlacementId =  "ca-app-pub-3940256099942544/4411468910";
        private const string TestRewardedPlacementId =      "ca-app-pub-3940256099942544/5224354917";
#endif

        public const string AdmobSdk = "admob";

        private LifeTime _lifeTime;
        private string _platformName;
        private ReactiveValue<bool> _isInitialized = new();

        private string _activePlacement = string.Empty;
        private bool _isInProgress;
        private float _reloadAdsInterval;
        private float _lastAdsReloadTime;
        private bool _loadingAds;

        private List<AdsShowResult> _rewardedHistory = new();
        private Dictionary<string,AdsShowResult> _awaitedRewards = new();
        private ReactiveCommand<AdsShowResult> _applyRewardedCommand = new();
        private Subject<AdsActionData> _adsAction = new();
        private Dictionary<string,PlatformAdsPlacement> _placements = new();

        private InterstitialAd _interstitialAdCache = null;

        private Dictionary<string, AdmobRewardedAdsCache> _rewardedAdsCache = new();

        public AdmobAdsService(
            string platformName,
            AdsDataConfiguration config,
            Dictionary<string,PlatformAdsPlacement> placements)
        {
            GameLog.Log($"[AdmobAdsService] create ads service", Color.cyan);

            _platformName = platformName;
            _lifeTime = new LifeTime();
            _reloadAdsInterval = config.reloadAdsInterval;
            _lastAdsReloadTime = -_reloadAdsInterval;
            _placements = placements;
 
            SubscribeToEvents();
        }

        public ILifeTime LifeTime => _lifeTime;

        public Observable<AdsActionData> AdsAction => _adsAction;
        public bool IsInProgress => _isInProgress;

        public async UniTask InitializeAsync()
        {
            GameLog.Log($"[AdmobAdsService] initialize ads service: " +
                        $"runtimePlatform={Application.platform}, provider={_platformName}", Color.cyan);

            MobileAds.Initialize(SdkInitializationCompletedEvent);
            
            var isInitialized = await _isInitialized
                .Where(x => x)
                .FirstAsync(_lifeTime.Token);

            GameLog.Log($"[AdmobAdsService] initialized ads service complete: " +
                        $"runtimePlatform={Application.platform}, provider={_platformName}", Color.cyan);

            foreach (PlatformAdsPlacement placementData in _placements.Values)
            {
                switch (placementData.placementType)
                {
                    case PlacementType.Interstitial:
                        LoadInterstitialAd(placementData.id).Forget();
                        break;
                    case PlacementType.Rewarded:
                        _rewardedAdsCache.Add(placementData.id, new AdmobRewardedAdsCache(placementData.platformPlacement));
                        LoadRewardedAd(placementData.id).Forget();
                        break;
                }
            }

            _applyRewardedCommand
                .Subscribe(ApplyRewardedCommand)
                .AddTo(_lifeTime);
        }

        public void LoadAdsAction(AdsActionData actionData)
        {
            if (actionData.PlacementType == PlacementType.Interstitial && actionData.ActionType == PlacementActionType.Closed)
                LoadInterstitialAd(actionData.PlacementName).Forget();
        }

        public async UniTask<bool> LoadRewardedAd(string placementId)
        {
            if (!_placements.TryGetValue(placementId, out var placementData))
            {
                GameLog.LogError($"[AdmobAdsService] haven't ads with id {placementId}");
                return false;
            }

            if (!_rewardedAdsCache.ContainsKey(placementId))
            {
                GameLog.LogError($"[AdmobAdsService] haven't ads with id {placementId} into rewarded map");
                return false;
            }
            
            if (_rewardedAdsCache[placementId].LoadProcess == true)
            {
                await UniTask
                    .WaitWhile(_rewardedAdsCache, x => x[placementId].LoadProcess == true,cancellationToken:_lifeTime);
                
                return false;
            }
            
            var adsRewardedAd = _rewardedAdsCache[placementId].RewardedAd;
            
            if (adsRewardedAd != null) return true;

            var adRequest = new AdRequest();
            var loadComplete = false;
            var loaded = false;
            var cppId = GetPlatformPlacementID(placementId); 
            _rewardedAdsCache[placementId].LoadProcess = true;

            GameLog.Log($"[AdmobAdsService] load rewarded ad: " +
                        $"runtimePlatform={Application.platform}, provider={_platformName}, " +
                        $"placement={placementId}, adUnitId={cppId}", Color.cyan);
            
            RewardedAd.Load(cppId, adRequest, (ad, error) =>
            {
                if (error != null || ad == null)
                {
                    LogAdLoadFailed("rewarded", placementId, cppId, error);
                    loadComplete = true;
                    ReloadAds(placementId).Forget();
                    return;
                }

                LogAdLoaded("rewarded", placementId, cppId, ad.GetResponseInfo());
                _rewardedAdsCache[placementId].RewardedAd = ad;
                
                SubscribeToRewardedAdEvents(ad);
                loaded = true;
                loadComplete = true;
            });

            await UniTask
                .WaitWhile(() => loadComplete == false)
                .AttachExternalCancellation(_lifeTime.Token);
            
            await UniTask.SwitchToMainThread();

            _rewardedAdsCache[placementId].LoadProcess = false;
            _rewardedAdsCache[placementId].Available = loaded;    
            
            GameLog.Log($"[AdmobAdsService] {placementId} load complete, status: {loaded}", Color.cyan);
            return loaded;
        }

        private string GetPlatformPlacementID(string placementId)
        {
            return _placements[placementId].platformPlacement;
        }

        private void LogAdLoaded(string adType, string placementId, string adUnitId, ResponseInfo responseInfo)
        {
#if GAME_DEBUG && (UNITY_ANDROID || UNITY_IOS || UNITY_IPHONE)
            var loadedAdapter = responseInfo?.GetLoadedAdapterResponseInfo();
            var responseExtras = responseInfo?.GetResponseExtras();
            var mediationGroupName = string.Empty;
            responseExtras?.TryGetValue(MediationGroupNameKey, out mediationGroupName);

            GameLog.Log($"[AdmobAdsService] {adType} ad loaded: " +
                        $"runtimePlatform={Application.platform}, provider={_platformName}, " +
                        $"placement={placementId}, adUnitId={adUnitId}, " +
                        $"responseId={responseInfo?.GetResponseId() ?? string.Empty}, " +
                        $"adapter={loadedAdapter?.AdapterClassName ?? string.Empty}, " +
                        $"adSource={loadedAdapter?.AdSourceName ?? string.Empty}, " +
                        $"mediationGroup={mediationGroupName}", Color.cyan);
#else
            GameLog.Log($"[AdmobAdsService] {adType} ad loaded: " +
                        $"runtimePlatform={Application.platform}, provider={_platformName}, " +
                        $"placement={placementId}, adUnitId={adUnitId}, " +
                        $"responseId={responseInfo?.GetResponseId() ?? string.Empty}", Color.cyan);
#endif
        }

        private void LogAdLoadFailed(string adType, string placementId, string adUnitId, LoadAdError error)
        {
            GameLog.Log($"[AdmobAdsService] {adType} ad failed to load: " +
                             $"runtimePlatform={Application.platform}, provider={_platformName}, " +
                             $"placement={placementId}, adUnitId={adUnitId}, error={error}, " +
                             $"responseInfo={error?.GetResponseInfo()}",color:Color.red);
        }

        private void LogAdImpression(string adType, string placementId, ResponseInfo responseInfo)
        {
            var adUnitId = GetPlatformPlacementID(placementId);
            GameLog.Log($"[AdmobAdsService] {adType} ad impression: " +
                        $"runtimePlatform={Application.platform}, provider={_platformName}, " +
                        $"placement={placementId}, adUnitId={adUnitId}, " +
                        $"responseId={responseInfo?.GetResponseId() ?? string.Empty}", Color.cyan);
        }

        public async UniTask<bool> LoadInterstitialAd(string placementId)
        {
            if (!_placements.TryGetValue(placementId, out var placementData))
            {
                GameLog.LogError($"[AdmobAdsService] haven't ads with id {placementId}");
                return false;
            }

            if (_interstitialAdCache != null)
            {
                _interstitialAdCache.Destroy();
                _interstitialAdCache = null;
            }

            var adRequest = new AdRequest();
            var loadComplete = false;
            var loaded = false;
            var cppId = GetPlatformPlacementID(placementId); 

            GameLog.Log($"[AdmobAdsService] load interstitial ad: " +
                        $"runtimePlatform={Application.platform}, provider={_platformName}, " +
                        $"placement={placementId}, adUnitId={cppId}", Color.cyan);
            
            InterstitialAd.Load(cppId, adRequest,
                (ad, error) =>
                {
                    if (error != null || ad == null)
                    {
                        LogAdLoadFailed("interstitial", placementId, cppId, error);
                        loadComplete = true;
                        ReloadAds(placementId).Forget();
                        return;
                    }

                    LogAdLoaded("interstitial", placementId, cppId, ad.GetResponseInfo());
            
                    _interstitialAdCache = ad;
                    loadComplete = true;
                    loaded = true;
                });

            await UniTask
                .WaitWhile(() => loadComplete == false)
                .AttachExternalCancellation(_lifeTime.Token);

            GameLog.Log($"[AdmobAdsService] {placementId} load complete, status: {loaded}", Color.cyan);
            
            return loaded;
        }

        private async UniTask ReloadAds(string placementId)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(_reloadAdsInterval));

            var placement = _placements[placementId];

            switch (placement.placementType)
            {
                case PlacementType.Interstitial:
                    LoadInterstitialAd(placement.id).Forget();
                    break;
                case PlacementType.Rewarded:
                    _rewardedAdsCache[placementId] = new AdmobRewardedAdsCache(placement.platformPlacement);
                    LoadRewardedAd(placement.id).Forget();
                    break;
            }
        }
        
        public async UniTask<AdsShowResult> Show(string placementId)
        {
            if (!_placements.TryGetValue(placementId, out var placementItem))
            {
                GameLog.LogError($"[AdmobAdsService]: Placement not found: {placementId}");
                
                return new AdsShowResult
                {
                    Error = true,
                    Message = AdsMessages.PlacementNotFound,
                    PlacementType = PlacementType.Rewarded,
                    PlacementName = string.Empty,
                    Rewarded = false,
                };
            }
            
            var showResult = await Show(placementItem.platformPlacement, PlacementType.Rewarded);
            return showResult;
        }
        
        public async UniTask<AdsShowResult> Show(string placeId, PlacementType type)
        {
            GameLog.Log($"[AdmobAdsService]: show {placeId} {type}", Color.cyan);

            if (_isInProgress)
            {
                return new AdsShowResult()
                {
                    PlacementName = placeId,
                    Rewarded = false,
                    Error = true,
                    Message = AdsMessages.AdsAlreadyInProgress,
                    PlacementType = type,
                };
            }

            _isInProgress = true;
            _activePlacement = placeId;
            
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = placeId,
                Message = string.Empty,
                ActionType = PlacementActionType.Requested,
                PlacementType =type,
                SdkName = AdmobSdk,
            });

            if (!await IsPlacementAvailable(placeId))
            {
                AddPlacementResult(placeId,type,false,true,AdsMessages.PlacementCapped);
                return new AdsShowResult()
                {
                    PlacementName = placeId,
                    Rewarded = false,
                    Error = true,
                    Message = AdsMessages.PlacementCapped,
                    PlacementType = type,
                };
            }

            ShowPlacementAsync(placeId, type).Forget();

            await UniTask
                .WaitWhile(() => !_awaitedRewards.ContainsKey(placeId))
                .AttachExternalCancellation(_lifeTime.Token);
            
            await UniTask.SwitchToMainThread();
            
            AdsShowResult placementResult = _awaitedRewards[placeId];
            
            _awaitedRewards.Remove(placeId);
            _isInProgress = false;
            
            GameLog.Log($"[AdmobAdsService]: show {placeId} {type}. result: {placementResult.Rewarded} {placementResult.Message}", Color.cyan);
            
            return placementResult;
        }

        public async UniTask ShowPlacementAsync(string placeId, PlacementType type)
        {
            switch (type)
            {
                case PlacementType.Rewarded:
                    await ShowRewardedVideo(placeId);
                    break;
                case PlacementType.Interstitial:
                    await ShowInterstitialVideo(placeId);
                    break;
                case PlacementType.Banner:
                    AddPlacementResult(placeId,type,false,true,AdsMessages.PlacementCapped);
                    break;
            }
        }
        
        public async UniTask<AdsShowResult> Show(PlacementType type)
        {
            PlatformAdsPlacement adsPlacement = null;
            foreach (var placement in _placements)
            {
                var placementValue = placement.Value;
                if(placementValue.placementType != type)
                    continue;
                var isAvailable = await IsPlacementAvailable(placementValue.platformPlacement);
                if(!isAvailable) continue;
                adsPlacement = placementValue;
                break;
            }

            if (adsPlacement == null)
            {
                return new AdsShowResult
                {
                    Error = true,
                    Message = AdsMessages.PlacementNotFound,
                    PlacementType = type,
                    PlacementName = string.Empty,
                    Rewarded = false,
                };
            }
            
            var showResult = await Show(adsPlacement.platformPlacement, PlacementType.Rewarded);
            return showResult;
        }
        
        public virtual async UniTask<AdsShowResult> ShowRewardedAdAsync(string placeId)
        {
            var showResult = await Show(placeId, PlacementType.Rewarded);
            return showResult;
        }
        
        public virtual async UniTask<AdsShowResult> ShowInterstitialAdAsync(string placeId)
        {
            var showResult = await Show(placeId, PlacementType.Interstitial);
            return showResult;
        }
        
        public void ValidateIntegration()
        {
            
        }
        
        public async UniTask<bool> IsPlacementAvailable(string placementName)
        {
            if(_placements.TryGetValue(placementName,out PlatformAdsPlacement adsPlacement) == false)
            {
                GameLog.LogError($"[AdmobAdsService]: Placement not found: {placementName}");
                return false;
            }
            var placementType = adsPlacement.placementType;
            
            switch (placementType)
            {
                case PlacementType.Rewarded:
                    var rewardedAdStatus = _rewardedAdsCache[placementName].Available;
                    GameLog.Log($"[AdmobAdsService] Request placement {placementName} ready status: {rewardedAdStatus}", Color.cyan);
                    return rewardedAdStatus;
                case PlacementType.Interstitial:
                    var interstitialStatus = _interstitialAdCache != null;
                    GameLog.Log($"[AdmobAdsService] Request placement {placementName} ready status: {interstitialStatus}", Color.cyan);
                    return interstitialStatus;
            }
            
            return false;
        }

        public async UniTask<bool> IsPlacementAvailable(PlacementType placementName)
        {
            foreach (var platformAdsPlacement in _placements)
            {
                var value = platformAdsPlacement.Value;
                if(value.placementType != placementName)
                    continue;
                var isAvailable = await IsPlacementAvailable(value.platformPlacement);
                if(isAvailable) return true;
            }

            return false;
        }

        public virtual async UniTask LoadAdsAsync()
        {
            GameLog.LogError($"[AdmobAdsService]: common preload not implemented into admob");
        }
        
        public void Dispose()
        {
            GameLog.Log("[AdmobAdsService]: dispose", Color.cyan);
            _lifeTime.Terminate();

            foreach (var (key, val) in _rewardedAdsCache)
                UnsubscribeToRewardedAdEvents(_rewardedAdsCache[key].RewardedAd);
            
            UnsubscribeToInterstitialAdEvents(_interstitialAdCache);
        }
        
        private AdsShowResult AddPlacementResult(string placeId, 
            PlacementType type,bool rewarded, 
            bool error = false,string message = "")
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

            return result;
        }
        
        private void SubscribeToEvents()
        {
            _adsAction.Subscribe(LoadAdsAction).AddTo(_lifeTime);
        }
        
        #region rewarded block
        
        private async UniTask ShowRewardedVideo(string placeId)
        {
            if (!await IsPlacementAvailable(placeId))
            {
                var rewardedResult = new AdsShowResult { 
                    PlacementName = placeId, 
                    Rewarded = false, 
                    Error = true,
                    Message = AdsMessages.RewardedUnavailable
                };
                _applyRewardedCommand.Execute(rewardedResult);
                return;
            }
            
            var rewardedAd = _rewardedAdsCache[placeId].RewardedAd;
            
            if (rewardedAd != null && rewardedAd.CanShowAd())
            {
                _activePlacement = placeId;
                rewardedAd.Show(reward =>
                {
                    CompleteRewardedVideoAsync(new AdmobRewardedResult
                    {
                        Reward = reward,
                        PlacementId = _activePlacement,
                        Message = string.Empty,
                        Error = null,
                    }).Forget();
                });
            }
        }
        
        private async UniTask CompleteRewardedVideoAsync(AdmobRewardedResult adResult)
        {
            var placementId = _activePlacement;
            await UniTask.SwitchToMainThread();
            
            var rewarded = adResult.Reward != null;
            var adError = adResult.Error;
            var reward = adResult.Reward;
            
            var error = adError !=null ? adError.GetMessage() : string.Empty;
            var message = !rewarded ? error : adResult.Message;
            var rewardName = reward != null ? reward.Type : placementId;
            var rewardAmount = reward?.Amount ?? 0f;
            var errorCode = adError?.GetCode() ?? 0;
            
            if(!_awaitedRewards.TryGetValue(placementId,out var result))
            {
                var rewardedResult = new AdsShowResult { 
                    PlacementName = placementId, 
                    Rewarded = rewarded,
                    Error = !rewarded,
                    Message = message,
                    PlacementType = PlacementType.Rewarded,
                    RewardName = rewardName,
                    RewardAmount = (float)rewardAmount,
                };
                
                _applyRewardedCommand.Execute(rewardedResult);
            }
            
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = placementId,
                Message = message,
                ActionType = PlacementActionType.Rewarded,
                PlacementType = PlacementType.Rewarded,
                SdkName = AdmobSdk,
                Duration = 30,
                ErrorCode = errorCode,
            });
        }
        
        private void SubscribeToRewardedAdEvents(RewardedAd rewardedAd)
        {
            if(rewardedAd == null) return;
            
            rewardedAd.OnAdClicked += RewardedVideoOnAdClickedEvent;
            rewardedAd.OnAdPaid += RewardedVideoOnAdPaidEvent;
            rewardedAd.OnAdImpressionRecorded += RewardedVideoOnAdImpressionRecordedEvent;
            rewardedAd.OnAdFullScreenContentClosed += RewardedVideoOnAdFullScreenContentClosedEvent;
            rewardedAd.OnAdFullScreenContentFailed += RewardedVideoOnAdFullScreenContentFailedEvent;
            rewardedAd.OnAdFullScreenContentOpened += RewardedVideoOnAdFullScreenContentOpenedEvent;
        }
        
        private void UnsubscribeToRewardedAdEvents(RewardedAd rewardedAd)
        {
            if(rewardedAd == null) return;
            
            rewardedAd.OnAdClicked -= RewardedVideoOnAdClickedEvent;
            rewardedAd.OnAdPaid -= RewardedVideoOnAdPaidEvent;
            rewardedAd.OnAdImpressionRecorded -= RewardedVideoOnAdImpressionRecordedEvent;
            rewardedAd.OnAdFullScreenContentClosed -= RewardedVideoOnAdFullScreenContentClosedEvent;
            rewardedAd.OnAdFullScreenContentFailed -= RewardedVideoOnAdFullScreenContentFailedEvent;
            rewardedAd.OnAdFullScreenContentOpened -= RewardedVideoOnAdFullScreenContentOpenedEvent;
        }
        
        private void RewardedVideoOnAdClickedEvent()
        {
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = string.Empty,
                ActionType = PlacementActionType.Clicked,
                PlacementType = PlacementType.Rewarded,
                SdkName = AdmobSdk,
                Duration = 0,
                ErrorCode = 0,
            });
            
            GameLog.Log($"[AdmobAdsService] rewarded: on ad clicked", Color.cyan);
        }
        
        private void RewardedVideoOnAdPaidEvent(AdValue adValue)
        {
            GameLog.Log($"[AdmobAdsService] rewarded: on ad paid", Color.cyan);
        }
        
        private void RewardedVideoOnAdImpressionRecordedEvent()
        {
            var rewardedAd = _rewardedAdsCache[_activePlacement].RewardedAd;
            LogAdImpression("rewarded", _activePlacement, rewardedAd.GetResponseInfo());
        }
        
        private void RewardedVideoOnAdFullScreenContentClosedEvent()
        {
            KillRewardedAds(_activePlacement);
            
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = string.Empty,
                ActionType = PlacementActionType.Closed,
                PlacementType = PlacementType.Rewarded,
                SdkName = AdmobSdk,
            });
            GameLog.Log($"[AdmobAdsService] rewarded: on ad full screen closed", Color.cyan);
        }
        
        private void RewardedVideoOnAdFullScreenContentOpenedEvent()
        {
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = string.Empty,
                ActionType = PlacementActionType.Opened,
                PlacementType = PlacementType.Rewarded,
                SdkName = AdmobSdk,
            });
            
            GameLog.Log($"[AdmobAdsService] rewarded: on ad full screen opened", Color.cyan);
        }
        
        private void RewardedVideoOnAdFullScreenContentFailedEvent(AdError error)
        {
            CompleteRewardedVideoAsync(new AdmobRewardedResult
            {
                Reward = null,
                PlacementId = _activePlacement,
                Message = string.Empty,
                Error = error,
            }).Forget();
        }

        private void KillRewardedAds(string placementId)
        {
            if (!_rewardedAdsCache.TryGetValue(placementId, out var adsRewardedAd))
                return;
            
            var rewardedAd = adsRewardedAd.RewardedAd;
            if(rewardedAd == null) return;
            
            UnsubscribeToRewardedAdEvents(rewardedAd);
            
            rewardedAd.Destroy();
            adsRewardedAd.RewardedAd = null;

            LoadRewardedAd(placementId).Forget();
        }
        
        private void ApplyRewardedCommand(AdsShowResult result)
        {
            _rewardedHistory.Add(result);
            _awaitedRewards.TryAdd(result.PlacementName, result);
        }
        
        #endregion
        
        #region interstitial block

        private async UniTask ShowInterstitialVideo(string placeId)
        {
            if (!await IsPlacementAvailable(placeId))
            {
                var interstitialResult = new AdsShowResult { 
                    PlacementName = placeId, 
                    Rewarded = false, 
                    Error = true,
                    Message = AdsMessages.RewardedUnavailable
                };
                _applyRewardedCommand.Execute(interstitialResult);
                return;
            }
            
            if (_interstitialAdCache.CanShowAd())
            {
                SubscribeToInterstitialAdEvents(_interstitialAdCache);
                _interstitialAdCache.Show();
            }
        }
        
        private void SubscribeToInterstitialAdEvents(InterstitialAd interstitialAd)
        {
            if(interstitialAd == null)
                return;
            
            interstitialAd.OnAdClicked += InterstitialVideoOnAdClickedEvent;
            interstitialAd.OnAdPaid += InterstitialVideoOnAdPaidEvent;
            interstitialAd.OnAdImpressionRecorded += InterstitialVideoOnAdImpressionRecordedEvent;
            interstitialAd.OnAdFullScreenContentClosed += InterstitialVideoOnAdFullScreenContentClosedEvent;
            interstitialAd.OnAdFullScreenContentFailed += InterstitialVideoOnAdFullScreenContentFailedEvent;
            interstitialAd.OnAdFullScreenContentOpened += InterstitialVideoOnAdFullScreenContentOpenedEvent;
        }
        
        private void UnsubscribeToInterstitialAdEvents(InterstitialAd interstitialAd)
        {
            if(interstitialAd == null)
                return;
            
            interstitialAd.OnAdClicked -= InterstitialVideoOnAdClickedEvent;
            interstitialAd.OnAdPaid -= InterstitialVideoOnAdPaidEvent;
            interstitialAd.OnAdImpressionRecorded -= InterstitialVideoOnAdImpressionRecordedEvent;
            interstitialAd.OnAdFullScreenContentClosed -= InterstitialVideoOnAdFullScreenContentClosedEvent;
            interstitialAd.OnAdFullScreenContentFailed -= InterstitialVideoOnAdFullScreenContentFailedEvent;
            interstitialAd.OnAdFullScreenContentOpened -= InterstitialVideoOnAdFullScreenContentOpenedEvent;
        }
        
        private void InterstitialVideoOnAdClickedEvent()
        {
            var message = "Interstitial: on ad clicked";
            
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = message,
                ActionType = PlacementActionType.Clicked,
                PlacementType = PlacementType.Rewarded,
                SdkName = AdmobSdk,
            });

            GameLog.Log($"[AdmobAdsService] interstitial: on ad clicked", Color.cyan);
        }
        
        private void InterstitialVideoOnAdPaidEvent(AdValue adValue)
        {
            var placementId = _interstitialAdCache.GetAdUnitID();
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = placementId,
                Message = "Paid",
                ActionType = PlacementActionType.Rewarded,
                PlacementType = PlacementType.Interstitial,
                SdkName = AdmobSdk,
            });
            
            GameLog.Log($"[AdmobAdsService] interstitial: on ad paid", Color.cyan);
        }
        
        private void InterstitialVideoOnAdImpressionRecordedEvent()
        {
            LogAdImpression("interstitial", _activePlacement, _interstitialAdCache.GetResponseInfo());
        }
        
        private void InterstitialVideoOnAdFullScreenContentClosedEvent()
        {
            var message = "Interstitial: on ad full screen closed";
            
            _adsAction.OnNext(new AdsActionData
            {
                PlacementName = _activePlacement,
                Message = message,
                ActionType = PlacementActionType.Closed,
                PlacementType = PlacementType.Interstitial,
                SdkName = AdmobSdk,
            });

            GameLog.Log($"[AdmobAdsService] interstitial: on ad full screen closed", Color.cyan);
            
            AddPlacementResult(_activePlacement, PlacementType.Interstitial, true, false, message);
            UnsubscribeToInterstitialAdEvents(_interstitialAdCache);
            _interstitialAdCache?.Destroy();
            _interstitialAdCache = null;

            LoadInterstitialAd(_activePlacement).Forget();
        }
        private void InterstitialVideoOnAdFullScreenContentOpenedEvent()
        {
            var message = "Interstitial: on ad full screen opened";
            
            _adsAction.OnNext(new AdsActionData
            {
                PlacementName = _activePlacement,
                Message = message,
                ActionType = PlacementActionType.Opened,
                PlacementType = PlacementType.Interstitial,
                SdkName = AdmobSdk,
            });
            
            GameLog.Log($"[AdmobAdsService] interstitial: on ad full screen opened", Color.cyan);
        }
        private void InterstitialVideoOnAdFullScreenContentFailedEvent(AdError error)
        {
            var message = "Interstitial: on ad full screen failed";
            
            _adsAction.OnNext(new AdsActionData()
            {
                PlacementName = _activePlacement,
                Message = message,
                ActionType = PlacementActionType.Failed,
                PlacementType = PlacementType.Interstitial,
                SdkName = AdmobSdk,
            });
            
            AddPlacementResult(_activePlacement, PlacementType.Interstitial, false, true, message);
            UnsubscribeToInterstitialAdEvents(_interstitialAdCache);
            _interstitialAdCache.Destroy();
            Debug.Log(message);
        }
        
        #endregion
        
        private void SdkInitializationCompletedEvent(InitializationStatus status)
        {
#if GAME_DEBUG && (UNITY_ANDROID || UNITY_IOS || UNITY_IPHONE)
            if (status == null)
            {
                GameLog.LogWarning("[AdmobAdsService] Google Mobile Ads initialization status is null");
            }
            else
            {
                var adapterStatusMap = status.getAdapterStatusMap();
                if (adapterStatusMap == null)
                {
                    GameLog.LogWarning("[AdmobAdsService] Google Mobile Ads adapter status map is null");
                }
                else
                {
                    foreach (var adapterStatus in adapterStatusMap)
                    {
                        GameLog.Log($"[AdmobAdsService] Mediation adapter status: " +
                                    $"adapter={adapterStatus.Key}, " +
                                    $"state={adapterStatus.Value.InitializationState}, " +
                                    $"latencyMs={adapterStatus.Value.Latency}, " +
                                    $"description={adapterStatus.Value.Description}", Color.cyan);
                    }
                }
            }
#endif

            _isInitialized.Value = true;
        }
    }

    public struct AdmobRewardedResult
    {
        public Reward Reward;
        public AdError Error;
        public string PlacementId;
        public string Message;
    }
}
