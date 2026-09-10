namespace UniGame.Ads.Runtime
{
    using System;
    using System.Collections.Generic;
    using Cysharp.Threading.Tasks;
    using R3;
    using UniCore.Runtime.ProfilerTools;
    using UniGame.GameFlow.Runtime;
    using UniGame.Runtime.Rx;
    using UnityEngine;
#if ADMOB_ENABLED
    using GoogleMobileAds.Ump.Api;
#endif

    public sealed class AdmobConsentService : GameService, IAdsConsentService
    {
        private const string LogTag = "[AdsConsent]";

        private readonly ReactiveValue<bool> _canRequestAds = new(false);
        private readonly ReactiveValue<bool> _privacyOptionsRequired = new(false);
        private UniTask _updateConsentInfoTask;
        private bool _updateConsentInfoStarted;
        private bool _updateConsentInfoCompleted;
        private bool _consentInfoUpdated;
#if ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS) && GAME_DEBUG
        private static bool _debugConsentStateReset;
#endif

#if ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS)
        public bool CanRequestAds => _canRequestAds.Value;
#else
        public bool CanRequestAds => true;
#endif

        public bool PrivacyOptionsRequired => _privacyOptionsRequired.Value;

        public Observable<bool> CanRequestAdsChanged => _canRequestAds;

        public Observable<bool> PrivacyOptionsRequiredChanged => _privacyOptionsRequired;

        public async UniTask UpdateConsentInfoAsync()
        {
#if ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS)
            if (_updateConsentInfoCompleted)
                return;

            if (_updateConsentInfoStarted)
            {
                await _updateConsentInfoTask;
                _updateConsentInfoCompleted = true;
                return;
            }

            _updateConsentInfoStarted = true;
            _updateConsentInfoTask = UpdateConsentInfoInternalAsync();
            try
            {
                await _updateConsentInfoTask;
            }
            finally
            {
                _updateConsentInfoCompleted = true;
            }
#else
            _privacyOptionsRequired.Value = false;
            _canRequestAds.Value = true;
            Log($"consent update skipped on unsupported platform: platform={Application.platform}");
            await UniTask.CompletedTask;
#endif
        }

        public async UniTask ShowConsentFormAsync()
        {
#if ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS)
            await UpdateConsentInfoAsync();

            if (!_consentInfoUpdated)
            {
                Log("consent form show skipped: consent information update failed");
                return;
            }

            await UniTask.SwitchToMainThread();
            var formError = await LoadAndShowConsentFormIfRequiredAsync();
            UpdateConsentState();

            if (formError != null)
                LogError($"consent form failed: {formError.Message}");
            else
                Log("consent form completed or not required");

            Log($"consent form flow completed: canRequestAds={CanRequestAds}, " +
                $"privacyOptionsRequired={PrivacyOptionsRequired}");
#else
            await UniTask.CompletedTask;
#endif
        }

        public async UniTask GatherConsentAsync()
        {
#if ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS)
            await UpdateConsentInfoAsync();
            await ShowConsentFormAsync();
#else
            _privacyOptionsRequired.Value = false;
            _canRequestAds.Value = true;
            Log($"consent flow skipped on unsupported platform: platform={Application.platform}");
            await UniTask.CompletedTask;
#endif
        }

        private async UniTask UpdateConsentInfoInternalAsync()
        {
#if ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS)
            await UniTask.SwitchToMainThread();
            Log($"consent update started: platform={Application.platform}");

            var updateError = await UpdateConsentInfoRequestAsync(CreateRequestParameters());
            UpdateConsentState();

            if (updateError != null)
            {
                LogError($"consent update failed: {updateError.Message}");
                UpdateCanRequestAdsState();
                return;
            }

            _consentInfoUpdated = true;
            Log($"consent update completed: canRequestAds={CanRequestAds}, " +
                $"privacyOptionsRequired={PrivacyOptionsRequired}");
#endif
        }

        public async UniTask ShowPrivacyOptionsFormAsync()
        {
#if UNITY_EDITOR
            Log("privacy options form is not opened in Editor. Test this flow on a real Android or iOS device.");
            await UniTask.CompletedTask;
            return;
#elif ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS)
            Log("privacy options form requested");

            await UpdateConsentInfoAsync();

            if (!PrivacyOptionsRequired)
            {
                Log("privacy options form skipped: entry point is not required");
                return;
            }

            var showError = await ShowPrivacyOptionsFormInternalAsync();
            UpdateConsentState();

            if (showError != null)
            {
                LogError($"privacy options form failed: {showError.Message}. " +
                         "Check that the AdMob privacy message is published for this app id and supports privacy options.");
                return;
            }

            Log($"privacy options form completed: canRequestAds={CanRequestAds}, privacyOptionsRequired={PrivacyOptionsRequired}");
#else
            Log($"privacy options form skipped on unsupported platform: platform={Application.platform}");
            await UniTask.CompletedTask;
#endif
        }

        private static void Log(string message)
        {
            Debug.Log($"{LogTag} {message}");
        }

        private static void LogError(string message)
        {
            GameLog.LogError($"{LogTag} {message}");
        }

#if ADMOB_ENABLED && (UNITY_ANDROID || UNITY_IOS)
        private static ConsentRequestParameters CreateRequestParameters()
        {
#if GAME_DEBUG
            if (!_debugConsentStateReset)
            {
                ConsentInformation.Reset();
                _debugConsentStateReset = true;
                Log("consent debug state reset");
            }

            Log("consent debug mode enabled: geography=EEA");

            return new ConsentRequestParameters
            {
                TagForUnderAgeOfConsent = false,
                ConsentDebugSettings = new ConsentDebugSettings
                {
                    DebugGeography = DebugGeography.EEA,
                    TestDeviceHashedIds = new List<string>
                    {
                        "3695FCC79D61A706580CAE16D8383555"
                    }
                }
            };
#else
            return new ConsentRequestParameters
            {
                TagForUnderAgeOfConsent = false,
            };
#endif
        }

        private void UpdateConsentState()
        {
            UpdateCanRequestAdsState();
            UpdatePrivacyOptionsState();
        }

        private void UpdateCanRequestAdsState()
        {
            var canRequestAds = ConsentInformation.CanRequestAds();
            _canRequestAds.Value = canRequestAds;

            Log($"can request ads state updated: canRequestAds={canRequestAds}");
        }

        private void UpdatePrivacyOptionsState()
        {
            var required = ConsentInformation.PrivacyOptionsRequirementStatus ==
                           PrivacyOptionsRequirementStatus.Required;
            _privacyOptionsRequired.Value = required;

            Log($"privacy options state updated: status={ConsentInformation.PrivacyOptionsRequirementStatus}, required={required}");
        }

        private static UniTask<FormError> UpdateConsentInfoRequestAsync(
            ConsentRequestParameters requestParameters)
        {
            var completion = new UniTaskCompletionSource<FormError>();

            ConsentInformation.Update(requestParameters, error =>
            {
                completion.TrySetResult(error);
            });

            return completion.Task;
        }

        private static async UniTask<FormError> LoadAndShowConsentFormIfRequiredAsync()
        {
            var canRequestAds = ConsentInformation.CanRequestAds();
            var formAvailable = ConsentInformation.IsConsentFormAvailable();
            Log($"consent form decision: canRequestAds={canRequestAds}, " +
                $"formAvailable={formAvailable}, " +
                $"privacyOptionsRequired={ConsentInformation.PrivacyOptionsRequirementStatus}");

            if (canRequestAds)
            {
                Log("consent form skipped: canRequestAds=true");
                return null;
            }

            Log("consent form load requested");

            var loadResult = await LoadConsentFormAsync();
            if (loadResult.error != null)
            {
                LogError($"consent form load failed: {loadResult.error.Message}");
                return loadResult.error;
            }

            Log("consent form loaded: formCreated=true");
            Log("consent form show requested");

            var sortingTask = SetConsentCanvasSortingOrderAsync();
            var completion = new UniTaskCompletionSource<FormError>();
            loadResult.form.Show(error =>
            {
                if (error == null)
                    Log("consent form dismissed: error=null");
                else
                    LogError($"consent form dismissed with error: {error.Message}");

                completion.TrySetResult(error);
            });

            await sortingTask;

            return await completion.Task;
        }

        private static UniTask<ConsentFormLoadResult> LoadConsentFormAsync()
        {
            var completion = new UniTaskCompletionSource<ConsentFormLoadResult>();

            ConsentForm.Load((form, error) =>
            {
                completion.TrySetResult(new ConsentFormLoadResult(form, error));
            });

            return completion.Task;
        }

        private static async UniTask<bool> SetConsentCanvasSortingOrderAsync()
        {
            const int maxAttempts = 300;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                var canvas = FindConsentCanvas();
                if (canvas != null)
                {
                    canvas.overrideSorting = true;
                    canvas.sortingOrder = 10000;
                    return true;
                }

                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            return false;
        }

        private static Canvas FindConsentCanvas()
        {
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
            foreach (var canvas in canvases)
            {
                if (canvas == null || !canvas.gameObject.scene.IsValid())
                    continue;

                var rootName = canvas.transform.root.name;
                if (rootName.Equals("ConsentForm", StringComparison.Ordinal) ||
                    rootName.StartsWith("ConsentForm(", StringComparison.Ordinal))
                    return canvas;
            }

            return null;
        }

        private readonly struct ConsentFormLoadResult
        {
            public ConsentFormLoadResult(ConsentForm form, FormError error)
            {
                this.form = form;
                this.error = error;
            }

            public readonly ConsentForm form;
            public readonly FormError error;
        }

        private static UniTask<FormError> ShowPrivacyOptionsFormInternalAsync()
        {
            var completion = new UniTaskCompletionSource<FormError>();
            ConsentForm.ShowPrivacyOptionsForm(error => completion.TrySetResult(error));
            return completion.Task;
        }
#endif
    }
}
