namespace LiteEntitySystem
{
    public interface ILocalSingleton
    {
        void Update(float dt);
        void VisualUpdate(float dt);
        void Destroy();
    }
}