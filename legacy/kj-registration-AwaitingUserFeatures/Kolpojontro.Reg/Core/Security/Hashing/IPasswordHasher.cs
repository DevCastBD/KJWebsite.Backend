using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Security.Hashing
{
    public interface IPasswordHasher
    {
        string HashPassword(string password);
    }
}
