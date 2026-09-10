namespace UniGame.Ads.Runtime
{
    using Cysharp.Threading.Tasks;
    using UniGame.GameFlow.Runtime;

    public interface IAdsConsentFlow : IGameService
    {
        UniTask RunAsync();
    }
}
