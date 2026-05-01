using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public enum SaveResult
    {
        Inserted, Updated, SecurityErrorKeyPinned, SecurityErrorHardwareNotMatching
    }
}
