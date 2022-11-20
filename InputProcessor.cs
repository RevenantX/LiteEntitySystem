namespace LiteEntitySystem
{
    public abstract class InputProcessor
    {
        public abstract int ReadInputs(EntityManager manager, byte ownerId, byte[] data, int offset, int size);
        public abstract void GenerateAndWriteInputs(EntityManager manager, byte[] data, ref int offset);
        public abstract int GetInputsSize(EntityManager manager);
    }

    public class InputProcessor<TInput> : InputProcessor where TInput : unmanaged
    {
        public override unsafe int GetInputsSize(EntityManager manager)
        {
            return manager.GetControllers<HumanControllerLogic<TInput>>().Count * sizeof(TInput);
        }

        public override unsafe int ReadInputs(EntityManager manager, byte ownerId, byte[] data, int offset, int size)
        {
            fixed (byte* rawData = data)
            {
                foreach (var controller in manager.GetControllers<HumanControllerLogic<TInput>>())
                {
                    if(controller.InternalOwnerId != ownerId)
                        continue;
                    controller.ReadInput(*(TInput*)(rawData + offset));
                    offset += sizeof(TInput);
                }
            }

            return offset;
        }

        public override unsafe void GenerateAndWriteInputs(EntityManager manager, byte[] data, ref int offset)
        {
            fixed (byte* rawData = data)
            {
                foreach (var controller in manager.GetControllers<HumanControllerLogic<TInput>>())
                {
                    controller.GenerateInput(out var input);
                    *(TInput*)(rawData + offset) = input;
                    offset += sizeof(TInput);
                }
            }
        }
    }
}