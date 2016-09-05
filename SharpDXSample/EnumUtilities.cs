using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpDXSample
{
    public static class EnumUtilities
    {
        static public int GetCount<T>() where T : struct
        {
            return Enum.GetNames(typeof(T)).Length;
        }
    }
}
