using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Models
{
    public class RegisterDTO : AwaitingUser
    {
        //public string UsernameOrEmail { get; set; }
        public string HashedPassword { get; set; }
    }
}
