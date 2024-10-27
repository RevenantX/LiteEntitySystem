namespace LiteEntitySystem
{
    public interface ILocalSingleton
    {
        void Destroy();
    }
    
    public interface ILocalSingletonWithUpdate : ILocalSingleton
    {
        void Update(float dt);
        void VisualUpdate(float dt);
    }
}