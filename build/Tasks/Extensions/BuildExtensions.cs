using System;
using Microsoft.Build.Framework;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Build.Tasks;

internal static class BuildExtensions
{
    extension(ITaskItem item)
    {
        public T? GetMetadata<T>(string name, bool isRequired = false)
        {
            Type type = typeof(T);
            string meta = item.GetMetadata(name);
            bool isNullOrEmpty = string.IsNullOrEmpty(meta);
            object cast = meta;

            if (isRequired && isNullOrEmpty)
            {
                throw new ArgumentException($"The following attribute is required: {name} - Required Type: {typeof(T).Name}");
            }

            if (!isNullOrEmpty)
            {
                try
                {
                    if (type.IsAssignableTo(typeof(bool)))
                    {
                        cast = bool.Parse(meta);
                    }
                    else if (type.IsEnum)
                    {
                        cast = Enum.Parse(type, meta, true);
                    }
                    else if (type.IsAssignableTo(typeof(int)))
                    {
                        cast = int.Parse(meta);
                    }
                }
                catch (Exception exception)
                {
                    throw new ArgumentException(
                        $"Unable to parse Metadata attribute with Name = '{name}', Value = {meta}. {exception.GetBaseException().Message}");
                }

                if (cast is T value)
                {
                    return value;
                }
            }

            return default(T);
        }
    }
}
