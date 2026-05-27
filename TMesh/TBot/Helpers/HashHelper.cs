using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Helpers
{
    public static class HashHelper
    {

        public static int ColorIndexFromDeviceId(uint deviceId, int colorCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(colorCount, nameof(colorCount));

            uint x = deviceId;

            // Simple deterministic bit mixing
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;

            return (int)(x % (uint)colorCount);
        }
    }
}
