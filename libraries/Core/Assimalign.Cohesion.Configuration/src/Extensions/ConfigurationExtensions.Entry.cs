using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Configuration;



public static partial class ConfigurationExtensions
{
    extension(IConfigurationEntry entry)
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool IsValue(out IConfigurationValue? value)
        {
            ArgumentNullException.ThrowIfNull(entry);

            value = null;

            if (entry is IConfigurationValue)
            {
                value = (IConfigurationValue)entry;

                return true;
            }
            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="section"></param>
        /// <returns></returns>
        public bool IsSection(out IConfigurationSection? section)
        {
            ArgumentNullException.ThrowIfNull(entry);

            section = null;

            if (entry is IConfigurationSection)
            {
                section = (IConfigurationSection)entry;

                return true;
            }
            return false;
        }
    }


    extension(IConfigurationValue value)
    {
        public short ToInt16()
        {
            return short.Parse(value?.Value!);
        }

        public bool TryToInt16(out short cast)
        {
            return short.TryParse(value?.Value, out cast);
        }

        public int ToInt32()
        {
            return int.Parse(value?.Value!);
        }

        public bool TryToInt32(out int cast)
        {
            return int.TryParse(value?.Value, out cast);
        }

        public long ToInt64()
        {
            return long.Parse(value?.Value!);
        }

        public bool TryToInt64(out long cast)
        {
            return long.TryParse(value?.Value, out cast);
        }
    }
}
