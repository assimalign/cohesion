using System;
using System.Collections.Generic;
using System.Text;

namespace System;

public static class EnumExtensions
{
    extension<TEnum>(TEnum @enum) where TEnum : struct, Enum
    {
        
        public bool IsAny(params TEnum[]? values)
        {
            ArgumentNullException.ThrowIfNullOrNone(values);

            TEnum item = default;

            for (int i = 0; i < values.Length; i++)
            {
                item = values[i];

                if (@enum.Equals(item))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
