using System.Globalization;

namespace LiteEntitySystem
{
    public readonly struct FloatAngle
    {
        public readonly float Value;

        public FloatAngle(float value)
        {
            Value = value;
        }

        public override string ToString()
        {
            return Value.ToString(CultureInfo.InvariantCulture);
        }

        public static implicit operator FloatAngle(float value)
        {
            return new FloatAngle(value);
        }
        
        public static implicit operator float(FloatAngle value)
        {
            return value.Value;
        }

        public static FloatAngle Lerp(FloatAngle a, FloatAngle b, float t)
        {
            return b;
        }
    }
}