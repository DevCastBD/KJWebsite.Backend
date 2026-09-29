using Kolpojontro.Reg.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Security
{
    public interface ITokenHandler
    {
        string CreateAccessToken(ApplicationUser user);
    }
}
