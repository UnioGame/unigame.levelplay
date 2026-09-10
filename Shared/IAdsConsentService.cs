namespace UniGame.Ads.Runtime
{
    using Cysharp.Threading.Tasks;
    using R3;
    using UniGame.GameFlow.Runtime;

    public interface IAdsConsentService : IGameService
    {
        bool CanRequestAds { get; }
        bool PrivacyOptionsRequired { get; }
        Observable<bool> CanRequestAdsChanged { get; }
        Observable<bool> PrivacyOptionsRequiredChanged { get; }

        UniTask UpdateConsentInfoAsync();
        UniTask ShowConsentFormAsync();
        UniTask GatherConsentAsync();
        UniTask ShowPrivacyOptionsFormAsync();
    }
}
