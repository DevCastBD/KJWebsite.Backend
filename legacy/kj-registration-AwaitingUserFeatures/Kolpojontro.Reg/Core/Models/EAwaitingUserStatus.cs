using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Models
{
    public enum EAwaitingUserStatus
    {
        Awaiting = 0,
        Approved = 1,
        Rejected = 2,
        Posponed = 3,
        Terminated = 4
    }
}
